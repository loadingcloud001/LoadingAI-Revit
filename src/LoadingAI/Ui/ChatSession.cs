using System.IO;
using System.Text.Json;
using LoadingAI.Gate;
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

    private AppSettings _settings = new();
    private ToolRegistry _registry = new();
    private List<DiscoveredPlugin> _discovered = new();
    private AgentLoop? _agent;
    private bool _isRunning;
    private bool _turnWrote;
    private bool _turnFailed;
    private CancellationTokenSource? _cts;
    private string _lastUserText = "";
    private readonly ToolAllowlist _allow = new();
    private readonly TurnChangeSet _changes = new();
    /// <summary>Element-id lists the tools saved this chat as @r1, @r2…; the model passes them back.</summary>
    private readonly ResultSetStore _sets = new();
    /// <summary>Images the tools produced this turn. The agent drains them after the round's tool results.</summary>
    private readonly List<AttachmentDto> _pendingImages = new();
    private string _turnId = "";
    private string _turnMode = "build";
    private bool _turnRolledBack;

    public ChatSession(WebHostBridge web, ApprovalGate approvals, Action<Action> onUi)
    {
        _web = web;
        _approvals = approvals;
        _onUi = onUi;
        Settings = ProviderSettings.Load();
    }

    public AppSettings Settings
    {
        get => _settings;
        set
        {
            var previousModel = _settings.ActiveProviderId;
            _settings = value ?? ProviderSettings.CreateDefault();
            AutoApproveGate.Enabled = ProviderSettings.LevelOf(_settings) == PermissionLevels.Bypass;
            if (_agent != null) _agent.CustomInstructions = _settings.Instructions;
            // A different model is a different partner: "allow for this session" ends with the switch.
            if (!string.Equals(previousModel, _settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase))
            {
                _allow.ClearSession();
                _sets.Clear();
            }
        }
    }

    public OpenAiCompatibleClient Client => _client;
    public ToolRegistry Registry => _registry;
    public IReadOnlyList<DiscoveredPlugin> Discovered => _discovered;
    public AgentLoop? Agent => _agent;
    public bool IsRunning => _isRunning;

    public void ReloadSettings() => Settings = ProviderSettings.Load();

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
        {
            _agent = new AgentLoop(_client, _registry);
            _agent.OnSteerDelivered = text =>
            {
                var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try { _onUi(() => _web.Post(new FromHostMessage.SteerAck(text, at))); } catch { }
            };
        }
        else
            _agent.ReplaceRegistry(_registry);
        _agent.DrainToolImages = () =>
        {
            lock (_pendingImages)
            {
                var list = _pendingImages.ToList();
                _pendingImages.Clear();
                return list;
            }
        };
        _agent.CustomInstructions = _settings.Instructions;
        ApplyPluginLoadWarnings();
    }

    public void ApplyPluginLoadWarnings()
    {
        if (_agent == null) return;
        var issues = PluginLoader.Issues.Where(i => !i.Notice).ToList();
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
            source = PluginAllowlist.Current.Source(Path.Combine(p.Meta.FolderPath, p.Meta.EntryAssembly)),
            removable = PluginLoader.CanRemove(p.Meta.Id, p.Meta.FolderPath, out _),
            tools = p.Meta.Tools.Select(t => new
            {
                name = t.Name,
                tier = t.Tier,
                description = t.Description,
                enabledByDefault = t.EnabledByDefault,
                enabled = ProviderSettings.IsToolEnabled(_settings, t.Name, t.EnabledByDefault),
                destructive = t.Destructive || p.Tools.Any(e => e.Destructive && string.Equals(e.Definition.Function.Name, t.Name, StringComparison.OrdinalIgnoreCase))
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
        if (PluginLoader.FailureCount > 0)
            message += $" {PluginLoader.FailureCount} plugin(s) failed to load.";
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

    /// <summary>
    /// A message the user sent while the agent works. It reaches the model before its next step. If no turn is
    /// running there is nothing to steer, so the pane is told to send it as an ordinary message.
    /// </summary>
    public void Steer(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return;
        if (!_isRunning || _agent == null)
        {
            _web.Post(new FromHostMessage.SteerAck(t));
            return;
        }
        _agent.Steer(t);
    }

    private bool IsAlwaysAllowed(string toolName)
        => _settings.AlwaysAllowTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers "Always allow" for a tool: saved with the settings, shown and revocable in Settings → Tools.</summary>
    private void AddAlways(string toolName)
    {
        if (IsAlwaysAllowed(toolName)) return;
        _settings.AlwaysAllowTools = new List<string>(_settings.AlwaysAllowTools) { toolName };
        try { ProviderSettings.Save(_settings); }
        catch (Exception ex) { HostLog.Write($"Saving the always-allow list failed: {ex.Message}"); }
        try { _onUi(() => _web.Post(new FromHostMessage.Settings(ProviderSettings.RedactForWeb(_settings)))); } catch { }
    }

    public void RestoreCheckpoint(List<ChatMessage> msgs) => _agent?.RestoreHistory(msgs);

    public void ClearHistory()
    {
        CancelAndDeny();
        _allow.ClearSession();
        _sets.Clear();
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
                _allow.ClearSession();
                _sets.Clear();
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
                _turnRolledBack = false;
                lock (_pendingImages) _pendingImages.Clear();
                _turnId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _turnMode = mode;
                var turnId = _turnId;
                var turnOutcome = "done";
                _changes.Reset();
                TurnChangeTracker.Begin(_changes);
                try { _onUi(() => _web.Post(new FromHostMessage.TurnStart(turnId, mode, ProviderSettings.LevelOf(_settings)))); } catch { }
                try
                {
                    var enrichedText = text;
                    if (ctxSnap != null && mentions.Count > 0)
                        enrichedText = text + "\n\n" + ctxSnap.ToPromptString(mentions);
                    else if (ctxSnap != null && (mode == "plan" || mode == "build"))
                        enrichedText = text + "\n\n" + ctxSnap.ToPromptString(new[] { "selection", "activeView", "warnings" });
                    var userMsg = attachments.Count > 0 ? ChatMessage.FromMultimodal("user", enrichedText, attachments) : ChatMessage.FromText("user", enrichedText);
                    var effectiveMode = mode == "auto" ? null : mode;
                    agent.AutoApprove = ProviderSettings.LevelOf(_settings) == PermissionLevels.Bypass;
                    agent.CustomInstructions = _settings.Instructions;
                    // What the model is likely to name: levels, categories, families. It never blocks the turn,
                    // and with no model open there is nothing to name, so the block goes.
                    agent.PromptExtras = await CollectPromptSummaryAsync(ct);
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

                    var assimilate = ShouldAssimilateTurn(result);
                    if (await FinishTurnGroupAsync(assimilate)) _turnRolledBack = true;
                    if (assimilate) await ReportChangesAsync(turnId);

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
                    turnOutcome = "cancelled";
                    if (await FinishTurnGroupAsync(assimilate: false)) _turnRolledBack = true;
                    progress.Report(ProgressSnapshot.Cancelled());
                    _onUi(() =>
                    {
                        _web.Post(new FromHostMessage.Error("Cancelled."));
                        _web.Post(new FromHostMessage.Status(GetStatusText()));
                    });
                }
                catch (Exception ex)
                {
                    turnOutcome = "error";
                    if (await FinishTurnGroupAsync(assimilate: false)) _turnRolledBack = true;
                    progress.Report(ProgressSnapshot.Error(ex.Message));
                    _onUi(() => _web.Post(new FromHostMessage.Error(ex.Message)));
                }
                finally
                {
                    if (await FinishTurnGroupAsync(assimilate: false)) _turnRolledBack = true;
                    TurnChangeTracker.End();
                    BuiltinQuota.ClearTicket();
                    var rolledBack = _turnRolledBack;
                    var undelivered = agent.DrainUndeliveredSteers();
                    _onUi(() =>
                    {
                        // Steer messages the turn ended before delivering go back to the pane as ordinary messages.
                        foreach (var t in undelivered)
                            try { _web.Post(new FromHostMessage.SteerAck(t)); } catch { }
                        try { _web.Post(new FromHostMessage.TurnEnd(turnId, turnOutcome, rolledBack ? true : null)); } catch { }
                        _isRunning = false;
                        try { _cts?.Dispose(); _cts = null; } catch { }
                    });
                }
            }
        });
    }

    /// <summary>
    /// What the model is likely to name: levels, categories, families. The adapter keeps the index and
    /// drops it when the model changes, so this does not walk the model again. Null when there is no
    /// model or the index cannot be read: the turn then runs without the block.
    /// </summary>
    private async Task<string?> CollectPromptSummaryAsync(CancellationToken ct)
    {
        var bridge = App.Bridge;
        if (bridge == null) return null;
        try { return (await new AdapterRevitBridge(bridge).GetProjectIndexAsync(ct)).ToPromptSummary(); }
        catch { return null; }
    }

    /// <summary>
    /// Runs one tool call for the agent. The gate (ADR-0017) decides first: the call runs, asks the user once in
    /// chat, or is blocked. Every call is announced to the pane as a tool_start and closed with a tool_end, so the
    /// transcript shows what happened without parsing text.
    /// </summary>
    public async Task<string> ExecuteToolAsync(string name, string argsJson)
    {
        var callId = Guid.NewGuid().ToString("N").Substring(0, 8);
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            return await ExecuteToolCoreAsync(callId, startedAt, name, argsJson);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The agent loop turns the throw into "Tool error: ..."; the pane gets the matching step end.
            EndStep(callId, false, "error", $"Tool error: {ex.Message}", startedAt);
            throw;
        }
    }

    private async Task<string> ExecuteToolCoreAsync(string callId, long startedAt, string name, string argsJson)
    {
        if (!_registry.TryGet(name, out var entry))
        {
            var hint = ToolDiagnostics.DescribeMissing(
                name,
                _registry.GetToolDefinitions().Select(d => d.Function.Name).ToList(),
                _discovered.SelectMany(p => p.Tools.Select(t => (p.Meta.Id, t.Definition.Function.Name))).ToList());
            StartStep(callId, name, "L1", false, argsJson, startedAt);
            return EndStep(callId, false, "error", hint, startedAt);
        }

        StartStep(callId, name, entry.Tier.ToString(), entry.Destructive, argsJson, startedAt);

        var schema = entry.Definition.Function.Parameters;
        // "@rN" stands for the ids a previous tool result saved: they are put back before the check.
        if (!ResultSetExpander.TryExpand(argsJson, _sets, out argsJson, out var setError))
            return EndStep(callId, false, "error", $"Tool argument error: {setError}", startedAt);
        argsJson = ToolArgumentCoercer.Coerce(schema, argsJson);
        if (!ToolArgumentValidator.Validate(schema, argsJson, out argsJson, out var validationError))
            return EndStep(callId, false, "error", $"Tool argument error: {validationError}", startedAt);

        var decision = GatePolicy.Decide(new GateRequest
        {
            Level = ProviderSettings.LevelOf(_settings),
            Mode = _turnMode,
            Tier = entry.Tier,
            Destructive = entry.Destructive,
            NeverAllowList = entry.NeverAllowList,
            DryRun = ToolPresets.IsDryRun(argsJson),
            HasPresets = ToolPresets.HasPresets(entry),
            SessionAllowed = _allow.IsSessionAllowed(name),
            AlwaysAllowed = IsAlwaysAllowed(name)
        });

        if (decision == GateDecision.Block)
            return EndStep(callId, false, "blocked", ReviewText.BlockedResult(name), startedAt);

        var edited = false;
        if (decision == GateDecision.Ask)
        {
            var review = await _approvals.ReviewAsync(
                callId, entry, name, argsJson, new ReviewContext(App.Bridge is { HasActiveTurnGroup: true }));
            switch (review.Outcome)
            {
                case ReviewOutcome.Denied:
                    MarkTurnFailedIfGroupActive();
                    return EndStep(callId, false, "denied", ReviewText.DenyResult(review.Reason), startedAt);
                case ReviewOutcome.Cancelled:
                    MarkTurnFailedIfGroupActive();
                    return EndStep(callId, false, "cancelled", "Cancelled by user.", startedAt);
                case ReviewOutcome.TimedOut:
                    MarkTurnFailedIfGroupActive();
                    return EndStep(callId, false, "timeout", ReviewText.TimeoutResult(), startedAt);
            }

            // The Run form or an edit changed the arguments: check them against the schema again.
            if (!ResultSetExpander.TryExpand(review.ArgsJson, _sets, out var reviewedArgs, out var reviewedSetError))
                return EndStep(callId, false, "error", $"Tool argument error: {reviewedSetError}", startedAt);
            argsJson = ToolArgumentCoercer.Coerce(schema, reviewedArgs);
            if (!ToolArgumentValidator.Validate(schema, argsJson, out argsJson, out var reviewedError))
                return EndStep(callId, false, "error", $"Tool argument error: {reviewedError}", startedAt);
            edited = review.Edited;

            // A tool on the never allow-list never enters an allow-list, whatever the card answered.
            if (GatePolicy.CanOfferAlways(entry.Destructive, ToolPresets.HasPresets(entry), entry.NeverAllowList))
            {
                if (review.Scope == ApprovalScope.Session) _allow.AddSession(name);
                else if (review.Scope == ApprovalScope.Always) AddAlways(name);
            }
        }

        var dryRun = ToolPresets.IsDryRun(argsJson);
        var isWrite = entry.Tier == ToolTier.L2 || (entry.Tier == ToolTier.L3 && !dryRun);
        if (isWrite)
        {
            var bridge = App.Bridge;
            if (bridge == null)
                return EndStep(callId, false, "error", "Please open a model first.", startedAt);
            await bridge.EnsureTurnGroupAsync(_cts?.Token ?? default);
        }

        ProgressReporter.Current.Report($"Running {name}");
        var execution = await entry.ExecuteAsync(argsJson);
        var result = execution.Text;

        // A tool that produced images: the pane gets a thumbnail, the model gets the files themselves.
        string? thumb = null;
        if (execution.Images is { Count: > 0 })
        {
            foreach (var img in execution.Images)
            {
                var att = ImageData.ToAttachment(img);
                if (att == null) continue;
                lock (_pendingImages) _pendingImages.Add(att);
                thumb ??= att.DataUrl;
            }
        }

        if (isWrite)
        {
            if (execution.WriteCertainty == ToolWriteCertainty.Committed)
                _turnWrote = true;
            if (!execution.IsSafeToAssimilate)
                MarkTurnFailedIfGroupActive();
        }
        else if (execution.Status is ToolExecutionStatus.Cancelled or ToolExecutionStatus.Unknown)
        {
            // A read or a dry run that merely found nothing ("Please select elements first.") has
            // changed nothing, so it must not undo the writes before it. A cancelled or unknown
            // outcome still does: the turn can no longer be trusted.
            MarkTurnFailedIfGroupActive();
        }

        // The ids the tool found are kept for the rest of the chat: the model may pass "@rN" back.
        if (execution.ResultIds is { Count: > 0 } && execution.Status is ToolExecutionStatus.Succeeded or ToolExecutionStatus.Partial)
        {
            var handle = _sets.Save(execution.ResultIds);
            if (handle != null) result += $"\nSaved as {handle} ({execution.ResultIds.Count} elements).";
        }

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

        // The model should know the call did not run exactly as it asked.
        if (edited)
            result += $"\n(The user edited the inputs before allowing this call. It ran with: {argsJson})";

        var ok = execution.Status is ToolExecutionStatus.Succeeded or ToolExecutionStatus.Partial;
        var outcome = execution.Status switch
        {
            ToolExecutionStatus.Succeeded or ToolExecutionStatus.Partial => "ran",
            ToolExecutionStatus.Cancelled => "cancelled",
            _ => "error"
        };
        return EndStep(callId, ok, outcome, result, startedAt, execution.Status == ToolExecutionStatus.Partial, thumb);
    }

    private void StartStep(string callId, string name, string tier, bool destructive, string argsJson, long startedAt)
    {
        var turnId = _turnId;
        try { _onUi(() => _web.Post(new FromHostMessage.ToolStart(turnId, callId, name, tier, destructive, argsJson, startedAt))); } catch { }
    }

    /// <summary>Closes the step in the pane and returns the text the model reads.</summary>
    private string EndStep(string callId, bool ok, string outcome, string text, long startedAt, bool partial = false, string? image = null)
    {
        var ms = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt);
        var summary = FirstLine(text, 140);
        var body = text.Length > 6000 ? text.Substring(0, 6000) + "…" : text;
        try { _onUi(() => _web.Post(new FromHostMessage.ToolEnd(callId, ok, outcome, summary, body, ms, partial ? true : null, image))); } catch { }
        return text;
    }

    private static string FirstLine(string text, int max)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return line.Length > max ? line.Substring(0, max - 1) + "…" : line;
        }
        return "";
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
    /// Returns true when it rolled back a group that had started.
    /// </summary>
    private static async Task<bool> FinishTurnGroupAsync(bool assimilate)
    {
        var bridge = App.Bridge;
        if (bridge is not { HasActiveTurnGroup: true }) return false;
        try
        {
            if (assimilate)
            {
                await bridge.AssimilateTurnGroupAsync(CancellationToken.None);
                return false;
            }
            await bridge.RollbackTurnGroupAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            HostLog.Write($"Turn TransactionGroup finish from HostPage failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// After a turn is assimilated: post what it changed, and select the added and modified elements when the
    /// user asked for that. The receipt is what Revit reported, not what the model claimed.
    /// </summary>
    private async Task ReportChangesAsync(string turnId)
    {
        TurnChangesSnapshot snap;
        try { snap = _changes.Snapshot(); }
        catch (Exception ex) { HostLog.Write($"Change snapshot failed: {ex.Message}"); return; }
        if (snap.IsEmpty) return;

        var payload = snap.ByCategory
            .Select(c => new FromHostMessage.CategoryChangesPayload(c.Category, c.Added, c.Modified, c.Deleted, c.Ids))
            .ToList();
        try
        {
            _onUi(() => _web.Post(new FromHostMessage.TurnChanges(
                turnId, snap.Added, snap.Modified, snap.Deleted, snap.IdsTruncated ? true : null, payload)));
        }
        catch { }

        if (!_settings.SelectChangedAfterTurn) return;
        var ids = snap.AllIds;
        if (ids.Count == 0) return;
        try { await ElementActions.SelectAsync(ids.ToList()); }
        catch (Exception ex) { HostLog.Write($"Selecting changed elements failed: {ex.Message}"); }
    }
}
