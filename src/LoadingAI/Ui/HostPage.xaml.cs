using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using Microsoft.Web.WebView2.Core;
using LoadingAI.Llm;
using LoadingAI.Plugins.Core;
using LoadingAI.Progress;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Ui;

public partial class HostPage : Page
{
    private static readonly JsonSerializerOptions HostJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private AppSettings _settings = ProviderSettings.Load();
    private readonly OpenAiCompatibleClient _client = new();
    private ToolRegistry _registry = new();
        private List<DiscoveredPlugin> _discovered = new();
    private AgentLoop? _agent;
    private bool _isRunning;
    private CancellationTokenSource? _cts;
    private string _lastUserText = "";
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<(bool approved, string? valuesJson)>> _pendingPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolEntry> _pendingPresetTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _approvalLock = new();

    public HostPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private bool _isWebInitialized;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isWebInitialized) return;
        _isWebInitialized = true;

        _settings = ProviderSettings.Load();
        InitTools();

        try
        {
            await Web.EnsureCoreWebView2Async();
#if DEBUG
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
            try { Web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived; } catch { }
            Web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            var distPath = Path.Combine(Path.GetDirectoryName(typeof(HostPage).Assembly.Location)!, "Web", "dist", "index.html");
            if (!File.Exists(distPath))
            {
                var alt = Path.Combine(Path.GetFullPath(Path.Combine(typeof(HostPage).Assembly.Location, @"..\..\..\..\Web\dist\index.html")));
                if (File.Exists(alt)) distPath = alt;
            }

            if (!File.Exists(distPath))
            {
                ShowFallback($"Web assets not found.\nExpected: {distPath}\n\nRun `npm run build` in web/ and rebuild the Add-in.");
                return;
            }

            var distDir = Path.GetDirectoryName(distPath)!;
            try { Web.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets", distDir, CoreWebView2HostResourceAccessKind.DenyCors); }
            catch (ArgumentException) { }

            Web.NavigationCompleted += OnNavigationCompleted;

            Web.Source = new Uri("https://appassets/index.html");
        }
        catch (Exception ex)
        {
            ShowFallback($"WebView2 failed to initialize:\n{ex.Message}\n\nEnsure WebView2 Evergreen runtime is installed.\nhttps://developer.microsoft.com/en-us/microsoft-edge/webview2/");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowFallback($"WebView2 navigation failed: {e.WebErrorStatus}\nUri: {sender?.ToString()}\n\nTry closing and reopening the pane. Press F12 for DevTools.");
            return;
        }
        LoadingText.Visibility = Visibility.Collapsed;
        PostToWeb(new { type = "settings", settings = ProviderSettings.RedactForWeb(_settings) });
        PostToWeb(new { type = "status", text = GetStatusText() });
        try { PostPlugins(); } catch { }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            if (Web.CoreWebView2 != null)
            {
                Web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                Web.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch { }
        _isWebInitialized = false;
    }

    private void InitTools()
    {
        var bridge = App.Bridge;
        if (bridge == null)
        {
            _agent = null;
            return;
        }
                    _discovered = PluginLoader.Discover(bridge, new PluginContextFactory(bridge));
        RebuildRegistry();
    }

    private void RebuildRegistry()
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

    private void ApplyPluginLoadWarnings()
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

    private void PostPlugins(string? extraStatus = null)
    {
        PostToWeb(new { type = "plugins", plugins = GetPluginsPayload(), loadIssues = PluginLoader.Issues });
        if (!string.IsNullOrWhiteSpace(extraStatus))
            PostToWeb(new { type = "status", text = extraStatus });
        else if (PluginLoader.Issues.Count > 0)
            PostToWeb(new { type = "status", text = $"{PluginLoader.Issues.Count} plugin(s) failed to load. See Settings → Tools." });
    }

    private object GetPluginsPayload()
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

    private void ShowFallback(string text)
    {
        LoadingText.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Collapsed;
        FallbackBorder.Visibility = Visibility.Visible;
        FallbackText.Text = text;
    }

    private void PostToWeb(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, HostJsonOptions);
            Web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch { }
    }

    private string GetStatusText()
    {
        var active = ProviderSettings.GetActive(_settings);
        if (active == null) return "No provider";
        if (DefaultLlm.IsBuiltin(active.Id))
            return $"Active: {DefaultLlm.DisplayName} | {DefaultLlm.DisplayModel}";
        return $"Active: {active.Name} | {active.BaseUrl} | {active.Model}";
    }

    /// <summary>Sends the free-tier allowance to the pane. Never includes the ticket.</summary>
    private void PostQuota(QuotaStatus quota)
    {
        PostToWeb(new
        {
            type = "quota",
            quota = new
            {
                providerId = DefaultLlm.Id,
                limit = quota.Limit,
                used = quota.Used,
                remaining = quota.Remaining,
                resetUtc = quota.ResetUtc,
                exhausted = quota.Exhausted,
                ok = quota.Ok,
                message = quota.Message
            }
        });
    }

    private ProviderConfig? ResolveProviderForTest(JsonDocument doc)
    {
        try
        {
            if (doc.RootElement.TryGetProperty("providerId", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                var id = idEl.GetString() ?? "";
                var found = _settings.Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (found != null) return DefaultLlm.BindForCall(found);
            }
            if (doc.RootElement.TryGetProperty("provider", out var provEl) && provEl.ValueKind == JsonValueKind.Object)
            {
                var prov = JsonSerializer.Deserialize<ProviderConfig>(provEl.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (prov == null) return null;
                // masked key -> lookup stored
                if (ProviderSettings.IsMaskedKey(prov.ApiKey) || string.IsNullOrWhiteSpace(prov.ApiKey))
                {
                    var stored = _settings.Providers.FirstOrDefault(p => p.Id.Equals(prov.Id, StringComparison.OrdinalIgnoreCase));
                    if (stored != null)
                        prov.ApiKey = stored.ApiKey;
                }
                if (string.IsNullOrWhiteSpace(prov.ApiKind)) prov.ApiKind = ProviderSettings.InferApiKind(prov);
                return DefaultLlm.BindForCall(prov);
            }
        }
        catch { }
        return null;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json = null;
        try
        {
            try { json = e.TryGetWebMessageAsString(); } catch { json = null; }
            if (string.IsNullOrEmpty(json))
            {
                try { json = e.WebMessageAsJson; } catch { json = null; }
            }
            if (string.IsNullOrEmpty(json)) return;
            if (json.Length > 2 && json[0] == '"' && json[json.Length - 1] == '"')
            {
                try { json = JsonSerializer.Deserialize<string>(json) ?? json; } catch { }
            }
        }
        catch (Exception ex)
        {
            try { PostToWeb(new { type = "error", text = $"Bridge receive error: {ex.Message}" }); } catch { }
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "requestSettings":
                    PostToWeb(new { type = "settings", settings = ProviderSettings.RedactForWeb(_settings) });
                    PostPlugins();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (!DefaultLlm.HasKey) return;
                            var quota = await _client.PeekBuiltinQuotaAsync();
                            Dispatcher.Invoke(() => PostQuota(quota));
                        }
                        catch { }
                    });
                    break;

                case "saveSettings":
                    {
                        var settingsJson = doc.RootElement.GetProperty("settings").GetRawText();
                        var s = JsonSerializer.Deserialize<AppSettings>(settingsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (s != null)
                        {
                            _settings = ProviderSettings.MergeFromWeb(_settings, s);
                            ProviderSettings.Save(_settings);
                            RebuildRegistry();
                            PostToWeb(new { type = "settings", settings = ProviderSettings.RedactForWeb(_settings) });
                            PostPlugins(GetStatusText());
                        }
                        break;
                    }

                case "getCatalog":
                    PostToWeb(new { type = "catalog", presets = ProviderCatalog.Presets });
                    break;

                case "testProvider":
                    {
                        var prov = ResolveProviderForTest(doc);
                        if (prov == null) { PostToWeb(new { type = "testResult", ok = false, message = "Provider not found." }); break; }
                        PostToWeb(new { type = "status", text = $"Testing {prov.Name}..." });
                        if (DefaultLlm.IsBuiltin(prov.Id))
                        {
                            _ = Task.Run(async () =>
                            {
                                var quota = await _client.PeekBuiltinQuotaAsync();
                                var msg = quota.Ok
                                    ? $"{DefaultLlm.DisplayName} is reachable. {quota.Remaining} of {quota.Limit} free requests left today."
                                    : quota.Message ?? $"{DefaultLlm.DisplayName} is unavailable.";
                                Dispatcher.Invoke(() =>
                                {
                                    PostQuota(quota);
                                    PostToWeb(new { type = "testResult", ok = quota.Ok, message = msg, providerId = prov.Id, models = new List<string> { DefaultLlm.DisplayModel } });
                                });
                            });
                            break;
                        }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var (ok, msg, models) = await _client.TestAsync(prov);
                                Dispatcher.Invoke(() => PostToWeb(new { type = "testResult", ok, message = msg, models, providerId = prov.Id }));
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.Invoke(() => PostToWeb(new { type = "testResult", ok = false, message = ex.Message, providerId = prov.Id }));
                            }
                        });
                        break;
                    }

                case "listModels":
                    {
                        var prov = ResolveProviderForTest(doc);
                        if (prov == null) { PostToWeb(new { type = "models", models = Array.Empty<string>(), error = "Provider not found." }); break; }
                        if (DefaultLlm.IsBuiltin(prov.Id))
                        {
                            PostToWeb(new { type = "models", providerId = prov.Id, models = new[] { DefaultLlm.DisplayModel } });
                            break;
                        }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var list = await _client.ListModelsAsync(prov);
                                Dispatcher.Invoke(() => PostToWeb(new { type = "models", providerId = prov.Id, models = list }));
                            }
                            catch (Exception ex) { Dispatcher.Invoke(() => PostToWeb(new { type = "models", providerId = prov.Id, models = Array.Empty<string>(), error = ex.Message })); }
                        });
                        break;
                    }

                case "listPlugins":
                    PostPlugins();
                    break;

                case "setPluginEnabled":
                    {
                        var toolName = doc.RootElement.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? "" : "";
                        var enabled = doc.RootElement.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrWhiteSpace(toolName))
                        {
                            _settings.Plugins[toolName] = enabled;
                            ProviderSettings.Save(_settings);
                            RebuildRegistry();
                            PostPlugins(enabled ? $"Enabled {toolName}" : $"Disabled {toolName}");
                        }
                        break;
                    }

                case "setPluginGroupEnabled":
                    {
                        var pluginId = doc.RootElement.TryGetProperty("pluginId", out var pid) ? pid.GetString() ?? "" : "";
                        var enabled = doc.RootElement.TryGetProperty("enabled", out var en2) && en2.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrWhiteSpace(pluginId))
                        {
                            var plugin = _discovered.FirstOrDefault(p => p.Meta.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
                            if (plugin != null)
                            {
                                foreach (var t in plugin.Meta.Tools) _settings.Plugins[t.Name] = enabled;
                                ProviderSettings.Save(_settings);
                                RebuildRegistry();
                                PostPlugins(enabled ? $"Enabled plugin {pluginId}" : $"Disabled plugin {pluginId}");
                            }
                        }
                        break;
                    }

                case "setPluginTierEnabled":
                    {
                        var tier = doc.RootElement.TryGetProperty("tier", out var tr) ? tr.GetString() ?? "" : "";
                        var enabled = doc.RootElement.TryGetProperty("enabled", out var en3) && en3.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrWhiteSpace(tier))
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
                            ProviderSettings.Save(_settings);
                            RebuildRegistry();
                            var l3 = _registry.Entries.Where(e => e.Tier == ToolTier.L3).Select(e => e.Definition.Function.Name).OrderBy(s => s).ToList();
                            var msg = enabled ? $"Enabled {n} {tier} tools." : $"Disabled {n} {tier} tools.";
                            if (l3.Count > 0) msg += " L3: " + string.Join(", ", l3) + ".";
                            else if (string.Equals(tier, "L3", StringComparison.OrdinalIgnoreCase)) msg += " No L3 tools on.";
                            PostPlugins(msg);
                        }
                        break;
                    }

                case "saveToolSettings":
                    {
                        ProviderSettings.Save(_settings);
                        RebuildRegistry();
                        var l3 = _registry.Entries.Where(e => e.Tier == ToolTier.L3).Select(e => e.Definition.Function.Name).OrderBy(s => s).ToList();
                        var msg = $"Saved. {_registry.GetToolDefinitions().Count} tools on.";
                        if (l3.Count > 0) msg += " L3: " + string.Join(", ", l3) + ".";
                        else msg += " No L3 tools on — use L3 all on.";
                        PostPlugins(msg);
                        break;
                    }

                case "removePlugin":
                    {
                        var pluginId = doc.RootElement.TryGetProperty("pluginId", out var pid) ? pid.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(pluginId) && !pluginId.Equals("core", StringComparison.OrdinalIgnoreCase))
                        {
                            var plugin = _discovered.FirstOrDefault(p => p.Meta.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
                            if (plugin != null && !string.IsNullOrWhiteSpace(plugin.Meta.FolderPath) && Directory.Exists(plugin.Meta.FolderPath))
                            {
                                var td = new TaskDialog("LoadingAI for Revit")
                                {
                                    MainInstruction = $"Remove plugin {pluginId}?",
                                    MainContent = plugin.Meta.FolderPath,
                                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                                    DefaultButton = TaskDialogResult.No
                                };
                                if (td.Show() != TaskDialogResult.Yes)
                                {
                                    PostToWeb(new { type = "status", text = "Remove cancelled." });
                                    break;
                                }
                                try { Directory.Delete(plugin.Meta.FolderPath, true); PostToWeb(new { type = "status", text = $"Removed {pluginId}. Restart Revit to unload." }); }
                                catch (Exception ex) { PostToWeb(new { type = "error", text = $"Remove failed: {ex.Message}. Close Revit and delete manually: {plugin.Meta.FolderPath}" }); }
                            }
                            else PostToWeb(new { type = "error", text = $"Plugin {pluginId} not found or is built-in." });
                        }
                        else PostToWeb(new { type = "error", text = "Cannot remove core plugin." });
                        break;
                    }

                case "reloadPlugins":
                    {
                        var bridge = App.Bridge;
                        if (bridge != null)
                        {
                                                    _discovered = PluginLoader.Discover(bridge, new PluginContextFactory(bridge));
                            RebuildRegistry();
                            var reloadMsg = $"Reloaded {_discovered.Count} plugins, {_registry.GetToolDefinitions().Count} tools enabled.";
                            if (PluginLoader.Issues.Count > 0)
                                reloadMsg += $" {PluginLoader.Issues.Count} plugin(s) failed to load.";
                            PostPlugins(reloadMsg);
                        }
                        break;
                    }

                case "approveTool":
                    {
                        var toolCallId = doc.RootElement.TryGetProperty("toolCallId", out var tc) ? tc.GetString() ?? "" : "";
                        var approved = doc.RootElement.TryGetProperty("approved", out var ap) && ap.ValueKind == JsonValueKind.True;
                        lock (_approvalLock)
                        {
                            if (_pendingApprovals.TryGetValue(toolCallId, out var tcs))
                            {
                                _pendingApprovals.Remove(toolCallId);
                                try { tcs.TrySetResult(approved); } catch { }
                            }
                        }
                        PostToWeb(new { type = "status", text = approved ? $"Approved {toolCallId}" : $"Denied {toolCallId}" });
                        break;
                    }

                case "submitPreset":
                    {
                        var toolCallId = doc.RootElement.TryGetProperty("toolCallId", out var ptc) ? ptc.GetString() ?? "" : "";
                        var approved = doc.RootElement.TryGetProperty("approved", out var pap) && pap.ValueKind == JsonValueKind.True;
                        string? valuesJson = null;
                        if (doc.RootElement.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Object)
                            valuesJson = vals.GetRawText();
                        CompletePreset(toolCallId, approved, valuesJson);
                        PostToWeb(new { type = "status", text = approved ? $"Confirmed preset {toolCallId}" : $"Cancelled preset {toolCallId}" });
                        break;
                    }

                case "presetPick":
                    {
                        var toolCallId = doc.RootElement.TryGetProperty("toolCallId", out var pk) ? pk.GetString() ?? "" : "";
                        var fieldId = doc.RootElement.TryGetProperty("fieldId", out var fid) ? fid.GetString() ?? "" : "";
                        await RunPresetPickAsync(toolCallId, fieldId);
                        break;
                    }

                case "listCheckpoints":
                    {
                        var cps = CheckpointStore.List();
                        PostToWeb(new { type = "checkpoints", checkpoints = cps.Select(c => new { id = c.Id, createdAt = c.CreatedAt, mode = c.Mode, preview = c.Preview, messageCount = c.MessageCount }).ToList() });
                        break;
                    }

                case "resumeCheckpoint":
                    {
                        var id = doc.RootElement.TryGetProperty("id", out var ide) ? ide.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(id)) { PostToWeb(new { type = "error", text = "Checkpoint id required." }); break; }
                        var msgs = CheckpointStore.Load(id);
                        if (msgs == null) { PostToWeb(new { type = "error", text = $"Checkpoint {id} not found." }); break; }
                        _agent?.RestoreHistory(msgs);
                        PostToWeb(new { type = "status", text = $"Resumed checkpoint {id} ({msgs.Count} messages)" });
                        PostToWeb(new { type = "history", messages = msgs.Select(m => new { role = m.Role, text = LlmJson.GetContentString(m.Content) }).ToList() });
                        break;
                    }

                case "clear":
                    _cts?.Cancel();
                    DenyAllPending();
                    _agent?.ClearHistory();
                    try { HistoryStore.Append("system", "Cleared."); } catch { }
                    break;

                case "cancel":
                    try { _cts?.Cancel(); DenyAllPending(); PostToWeb(new { type = "status", text = "Cancelling..." }); PostProgress(ProgressSnapshot.Cancelled()); } catch { }
                    break;

                case "send":
                    {
                        var text = doc.RootElement.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
                        var mode = doc.RootElement.TryGetProperty("mode", out var mEl) ? mEl.GetString() ?? "auto" : "auto";
                        var slash = doc.RootElement.TryGetProperty("slash", out var sl) ? sl.GetString() ?? "" : "";
                        var mentions = new List<string>();
                        if (doc.RootElement.TryGetProperty("mentions", out var men) && men.ValueKind == JsonValueKind.Array)
                            foreach (var el in men.EnumerateArray())
                                if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                                    mentions.Add(el.GetString()!.ToLowerInvariant());
                        // slash overrides mode
                        if (!string.IsNullOrWhiteSpace(slash))
                        {
                            if (slash.Equals("plan", StringComparison.OrdinalIgnoreCase)) mode = "plan";
                            else if (slash.Equals("ask", StringComparison.OrdinalIgnoreCase)) mode = "ask";
                            else if (slash.Equals("build", StringComparison.OrdinalIgnoreCase)) mode = "build";
                            else if (slash.Equals("clear", StringComparison.OrdinalIgnoreCase)) { _cts?.Cancel(); _agent?.ClearHistory(); try { HistoryStore.Append("system", "Cleared."); } catch { } PostToWeb(new { type = "status", text = "Cleared." }); break; }
                            else if (slash.Equals("help", StringComparison.OrdinalIgnoreCase))
                            {
                                var help = "Commands: /plan <goal> read-only plan | /build <goal> write and execute | /ask <question> read-only Q&A | /clear clear session | /help help | /export ifc/dwg/pdf | /isolate isolate selection | /hide hide selection | /resume resume | /checkpoints list checkpoints\nMentions: @selection @activeView @visible @warnings @levels @sheets";
                                PostToWeb(new { type = "assistant", text = help }); break;
                            }
                            else if (slash.Equals("checkpoints", StringComparison.OrdinalIgnoreCase))
                            {
                                var cps = CheckpointStore.List();
                                var txt = cps.Count == 0 ? "No checkpoints." : string.Join("\n", cps.Select(c => $"{c.Id} {c.CreatedAt:MM-dd HH:mm} {c.Mode} {c.Preview} ({c.MessageCount} msgs)"));
                                PostToWeb(new { type = "checkpoints", checkpoints = cps.Select(c => new { id = c.Id, createdAt = c.CreatedAt, mode = c.Mode, preview = c.Preview, messageCount = c.MessageCount }).ToList() });
                                PostToWeb(new { type = "assistant", text = txt }); break;
                            }
                            else if (slash.Equals("resume", StringComparison.OrdinalIgnoreCase))
                            {
                                var cps2 = CheckpointStore.List();
                                var last = cps2.FirstOrDefault();
                                if (last == null) { PostToWeb(new { type = "error", text = "No checkpoints to resume." }); break; }
                                var msgs2 = CheckpointStore.Load(last.Id);
                                if (msgs2 != null) { _agent?.RestoreHistory(msgs2); PostToWeb(new { type = "status", text = $"Resumed {last.Id}" }); PostToWeb(new { type = "history", messages = msgs2.Select(m => new { role = m.Role, text = LlmJson.GetContentString(m.Content) }).ToList() }); }
                                break;
                            }
                        }
                        // parse attachments
                        var attachments = new List<AttachmentDto>();
                        if (doc.RootElement.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in atts.EnumerateArray())
                            {
                                try
                                {
                                    var dataUrl = el.TryGetProperty("dataUrl", out var du) ? du.GetString() ?? "" : "";
                                    if (string.IsNullOrWhiteSpace(dataUrl)) continue;
                                    if (!dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) continue;
                                    if (dataUrl.Length * 0.75 > 4.5 * 1024 * 1024) { PostToWeb(new { type = "error", text = "Image too large (>4MB) — please compress or crop." }); continue; }
                                    var id = el.TryGetProperty("id", out var ide) ? ide.GetString() ?? Guid.NewGuid().ToString("N").Substring(0, 8) : Guid.NewGuid().ToString("N").Substring(0, 8);
                                    var name = el.TryGetProperty("name", out var ne) ? ne.GetString() ?? "image.png" : "image.png";
                                    var mt = el.TryGetProperty("mediaType", out var me) ? me.GetString() ?? "image/png" : "image/png";
                                    attachments.Add(new AttachmentDto(id, name, mt, dataUrl));
                                    if (attachments.Count >= 5) break;
                                }
                                catch { }
                            }
                        }
                        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0) break;
                        _lastUserText = text;
                        if (_isRunning)
                        {
                            PostToWeb(new { type = "error", text = "Already working, please wait or press Stop." });
                            break;
                        }

                        var provider = ProviderSettings.GetActiveForCall(_settings);
                        if (provider == null)
                        {
                            PostToWeb(new { type = "error", text = "No provider configured." });
                            break;
                        }
                        if (DefaultLlm.IsBuiltin(provider.Id))
                        {
                            if (!DefaultLlm.HasKey)
                            {
                                PostToWeb(new { type = "error", text = $"{DefaultLlm.DisplayName} is not available in this build." });
                                break;
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(provider.ApiKey) && provider.BaseUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            PostToWeb(new { type = "error", text = $"Provider '{provider.Name}' has no API key. Open Settings and paste your key." });
                            break;
                        }

                        if (_agent == null)
                        {
                            PostToWeb(new { type = "error", text = "Agent not ready. Restart Revit and reopen the pane." });
                            break;
                        }

                        _isRunning = true;

                        // The free tier counts one unit per send; the ticket it returns covers
                        // every model call this run makes.
                        if (DefaultLlm.IsBuiltin(provider.Id))
                        {
                            PostToWeb(new { type = "status", text = $"Checking {DefaultLlm.DisplayName} allowance..." });
                            var quota = await _client.ConsumeBuiltinQuotaAsync();
                            PostQuota(quota);
                            if (!quota.Ok)
                            {
                                _isRunning = false;
                                PostToWeb(new { type = "error", text = quota.Message ?? $"{DefaultLlm.DisplayName} is unavailable." });
                                break;
                            }
                        }

                        _cts?.Dispose();
                        _cts = new CancellationTokenSource();
                        var ct = _cts.Token;
                        var agent = _agent;
                        var progress = new ProgressReporter(snap =>
                        {
                            try { Dispatcher.Invoke(() => PostProgress(snap)); } catch { }
                        });
                        progress.Report(ProgressSnapshot.Thinking());
                        PostToWeb(new { type = "status", text = mode == "plan" ? "Planning..." : mode == "ask" ? "Thinking..." : "Working..." });
                        // auto context injection
                        ContextSnapshot? ctxSnap = null;
                        try
                        {
                            if (App.Bridge != null)
                            {
                                ctxSnap = await ContextCollector.CollectAsync(App.Bridge, ct);
                                Dispatcher.Invoke(() =>
                                {
                                    try { PostToWeb(new { type = "context", context = new { selectionCount = ctxSnap.SelectionCount, selectionPreview = ctxSnap.SelectionPreview, activeView = ctxSnap.ActiveViewName, visibleCount = ctxSnap.VisibleCount, warningCount = ctxSnap.WarningCount, warningPreview = ctxSnap.WarningPreview } }); } catch { }
                                });
                            }
                        }
                        catch { }

                        _ = Task.Run(async () =>
                        {
                            using (ProgressReporter.Push(progress))
                            {
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
                                        try { Dispatcher.Invoke(() => PostToWeb(new { type = "trace", text = trace })); } catch { }
                                    },
                                    delta =>
                                    {
                                        try { Dispatcher.Invoke(() => PostToWeb(new { type = "delta", text = delta })); } catch { }
                                    },
                                    todos =>
                                    {
                                        try { Dispatcher.Invoke(() => PostToWeb(new { type = "todo", todos = todos.Select(t => new { id = t.Id, title = t.Title, tool = t.Tool, status = t.Status, error = t.Error }).ToList() })); } catch { }
                                    },
                                    snap => progress.Report(snap),
                                    ct);

                                Dispatcher.Invoke(() =>
                                {
                                    if (!string.IsNullOrWhiteSpace(result))
                                    {
                                        PostToWeb(new { type = "assistant", text = result });
                                        try { HistoryStore.Append("assistant", result); HistoryStore.Trim(); } catch { }
                                        try { CheckpointStore.Save(agent.History, mode); } catch { }
                                        if (string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var draft = PlanParser.TryParse(result);
                                            if (draft != null)
                                            {
                                                PostToWeb(new
                                                {
                                                    type = "plan",
                                                    plan = new
                                                    {
                                                        title = draft.Title,
                                                        steps = draft.Steps.Select(s => new { title = s.Title, status = "pending" }).ToList(),
                                                        isStreaming = false
                                                    }
                                                });
                                            }
                                        }
                                    }
                                    PostToWeb(new { type = "status", text = GetStatusText() });
                                    PostProgress(ProgressSnapshot.Complete());
                                });
                            }
                            catch (OperationCanceledException)
                            {
                                progress.Report(ProgressSnapshot.Cancelled());
                                Dispatcher.Invoke(() =>
                                {
                                    PostToWeb(new { type = "error", text = "Cancelled." });
                                    PostToWeb(new { type = "status", text = GetStatusText() });
                                });
                            }
                            catch (Exception ex)
                            {
                                progress.Report(ProgressSnapshot.Error(ex.Message));
                                Dispatcher.Invoke(() => PostToWeb(new { type = "error", text = ex.Message }));
                            }
                            finally
                            {
                                BuiltinQuota.ClearTicket();
                                Dispatcher.Invoke(() =>
                                {
                                    _isRunning = false;
                                    try { _cts?.Dispose(); _cts = null; } catch { }
                                });
                            }
                            }
                        }, ct);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            PostToWeb(new { type = "error", text = $"Bridge error: {ex.Message}" });
        }
    }

    private async Task<string> ExecuteToolAsync(string name, string argsJson)
    {
        if (!_registry.TryGet(name, out var entry))
        {
            var hint = ToolDiagnostics.DescribeMissing(
                name,
                _registry.GetToolDefinitions().Select(d => d.Function.Name).ToList(),
                _discovered.SelectMany(p => p.Tools.Select(t => (p.Meta.Id, t.Definition.Function.Name))).ToList());
            try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_result", text = hint })); } catch { }
            return hint;
        }

        if (ToolPresets.HasPresets(entry))
        {
            var preset = await AwaitPresetAsync(entry, name, argsJson);
            if (!preset.approved)
            {
                try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_result", text = $"{name} {preset.message}" })); } catch { }
                return preset.message;
            }
            argsJson = ToolPresets.Merge(argsJson, preset.valuesJson);
            // dryRun is read-only, so the write confirm is skipped. Generic arg check, not a tool-name list.
            if (entry.Tier == ToolTier.L3 && !ToolPresets.IsDryRun(argsJson))
            {
                if (!await ConfirmL3NativeAsync(name, argsJson))
                    return "Cancelled by user.";
            }
            else
            {
                try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_call", text = $"{name}: {argsJson}" })); } catch { }
            }
        }
        // L3: one host gate — Web approval (TCS) then native TaskDialog. Tools must not dialog.
        else if (entry.Tier == ToolTier.L3 && !ToolPresets.IsDryRun(argsJson))
        {
            var callId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_approvalLock) _pendingApprovals[callId] = tcs;
            try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_approval", approval = new { toolCallId = callId, toolName = name, tier = entry.Tier.ToString(), args = argsJson, description = entry.Definition.Function.Description } })); } catch { }
            try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_call", text = $"{name}: {argsJson} (awaiting approval {callId})" })); } catch { }
            var timeout = Task.Delay(TimeSpan.FromSeconds(180));
            var completed = await Task.WhenAny(tcs.Task, timeout);
            bool approved = false;
            if (completed == tcs.Task) approved = await tcs.Task;
            else
            {
                lock (_approvalLock) _pendingApprovals.Remove(callId);
                try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_result", text = $"{name} timed out waiting for approval (180s)" })); } catch { }
                return "Cancelled: approval timeout (180s).";
            }
            if (!approved)
            {
                try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_result", text = $"{name} denied by user" })); } catch { }
                return "Cancelled by user (denied).";
            }

            if (!await ConfirmL3NativeAsync(name, argsJson))
                return "Cancelled by user.";
        }
        else
        {
            try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_call", text = $"{name}: {argsJson}" })); } catch { }
        }

        string result;
        try
        {
            ProgressReporter.Current.Report($"Running {name}");
            result = await entry.Handler(argsJson);
        }
        catch (Exception ex) { result = $"Tool {name} failed: {ex.Message}"; }

        // Post result card
        try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_result", text = $"{name} result: {Truncate(result, 400)}" })); } catch { }

        // Audit for L2/L3
        if (entry.Tier != ToolTier.L1)
        {
            try
            {
                // Try to extract elementIds from args if present for audit
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

    private void PostProgress(ProgressSnapshot snap)
    {
        PostToWeb(new
        {
            type = "progress",
            progress = new
            {
                phase = snap.Phase,
                label = snap.Label,
                tool = snap.Tool,
                percent = snap.Percent,
                current = snap.Current,
                total = snap.Total,
                indeterminate = snap.Indeterminate
            }
        });
    }

    private async Task<(bool approved, string? valuesJson, string message)> AwaitPresetAsync(ToolEntry entry, string name, string argsJson)
    {
        var callId = Guid.NewGuid().ToString("N").Substring(0, 8);
        var tcs = new TaskCompletionSource<(bool approved, string? valuesJson)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_approvalLock)
        {
            _pendingPresets[callId] = tcs;
            _pendingPresetTools[callId] = entry;
        }

        var fields = entry.Presets.Select(f => new
        {
            id = f.Id,
            label = string.IsNullOrWhiteSpace(f.Label) ? f.Id : f.Label,
            kind = ToolPresets.KindName(f.Kind),
            required = f.Required,
            options = f.Options,
            category = f.Category,
            prompt = f.Prompt
        }).ToList();
        var values = ToolPresets.Prefill(entry.Presets, argsJson);

        try
        {
            Dispatcher.Invoke(() => PostToWeb(new
            {
                type = "tool_preset",
                preset = new
                {
                    toolCallId = callId,
                    toolName = name,
                    tier = entry.Tier.ToString(),
                    description = entry.Definition.Function.Description,
                    fields,
                    values
                }
            }));
        }
        catch { }
        try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_call", text = $"{name}: {argsJson} (awaiting preset {callId})" })); } catch { }

        var timeout = Task.Delay(TimeSpan.FromMinutes(15));
        Task completed;
        try
        {
            completed = await Task.WhenAny(tcs.Task, timeout);
        }
        catch (OperationCanceledException)
        {
            CompletePreset(callId, false, null);
            return (false, null, "Cancelled by user.");
        }

        if (completed != tcs.Task)
        {
            CompletePreset(callId, false, null);
            return (false, null, "Cancelled: preset timeout.");
        }

        var (approved, valuesJson) = await tcs.Task;
        return approved
            ? (true, valuesJson, "")
            : (false, null, "Cancelled by user.");
    }

    private void CompletePreset(string toolCallId, bool approved, string? valuesJson)
    {
        lock (_approvalLock)
        {
            _pendingPresetTools.Remove(toolCallId);
            if (_pendingPresets.TryGetValue(toolCallId, out var tcs))
            {
                _pendingPresets.Remove(toolCallId);
                try { tcs.TrySetResult((approved, valuesJson)); } catch { }
            }
        }
    }

    private void DenyAllPending()
    {
        lock (_approvalLock)
        {
            foreach (var kv in _pendingApprovals.ToList())
            {
                _pendingApprovals.Remove(kv.Key);
                try { kv.Value.TrySetResult(false); } catch { }
            }
            foreach (var kv in _pendingPresets.ToList())
            {
                _pendingPresets.Remove(kv.Key);
                _pendingPresetTools.Remove(kv.Key);
                try { kv.Value.TrySetResult((false, null)); } catch { }
            }
        }
    }

    private async Task<bool> ConfirmL3NativeAsync(string name, string argsJson)
    {
        var raw = App.Bridge;
        if (raw == null) return false;
        return await raw.RunAsync(_ =>
        {
            var td = new TaskDialog("LoadingAI for Revit")
            {
                MainInstruction = $"Confirm L3: {name}?",
                MainContent = Truncate(argsJson, 400) + "\n\nUndoable transaction. Default No.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            return td.Show() == TaskDialogResult.Yes;
        });
    }

    private async Task RunPresetPickAsync(string toolCallId, string fieldId)
    {
        ToolEntry? entry = null;
        lock (_approvalLock)
        {
            _pendingPresetTools.TryGetValue(toolCallId, out entry);
        }
        var field = entry?.Presets.FirstOrDefault(f => string.Equals(f.Id, fieldId, StringComparison.OrdinalIgnoreCase));
        if (entry == null || field == null)
        {
            PostToWeb(new { type = "presetPickResult", toolCallId, fieldId, ok = false, error = "No matching preset field." });
            return;
        }
        if (field.Kind is not PresetFieldKind.PickElements and not PresetFieldKind.PickBox)
        {
            PostToWeb(new { type = "presetPickResult", toolCallId, fieldId, ok = false, error = "Field is not a pick." });
            return;
        }

        var bridge = App.Bridge;
        if (bridge == null)
        {
            PostToWeb(new { type = "presetPickResult", toolCallId, fieldId, ok = false, error = "No Revit bridge." });
            return;
        }

        object result;
        try
        {
            if (field.Kind == PresetFieldKind.PickElements)
                result = await bridge.RunAsync(app => PresetPickHost.PickElements(app, field.Category, field.Prompt), PresetPickHost.PickTimeout);
            else
                result = await bridge.RunAsync(app => PresetPickHost.PickBox(app, field.Prompt), PresetPickHost.PickTimeout);
        }
        catch (Exception ex)
        {
            PostToWeb(new { type = "presetPickResult", toolCallId, fieldId, ok = false, error = ex.Message });
            return;
        }

        PostToWeb(new { type = "presetPickResult", toolCallId, fieldId, result });
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
}
