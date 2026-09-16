using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using Microsoft.Web.WebView2.Core;
using LoadingAI.Llm;
using LoadingAI.Plugins.Core;
using LoadingAI.Progress;

namespace LoadingAI.Ui;

public partial class HostPage : Page
{
    private readonly WebHostBridge _web;
    private readonly ApprovalGate _approvals;
    private readonly ChatSession _session;
    private bool _isWebInitialized;

    public HostPage()
    {
        InitializeComponent();
        _web = new WebHostBridge(() => Web.CoreWebView2);
        _approvals = new ApprovalGate(_web, OnUi);
        _session = new ChatSession(_web, _approvals, OnUi);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUi(Action action)
    {
        Dispatcher.Invoke(action);
    }

    private void PostToWeb(object payload) => _web.Post(payload);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isWebInitialized) return;
        _isWebInitialized = true;

        _session.ReloadSettings();
        _session.InitTools();

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
        PostToWeb(new { type = "settings", settings = ProviderSettings.RedactForWeb(_session.Settings) });
        PostToWeb(new { type = "status", text = _session.GetStatusText() });
        try { PostPlugins(); } catch { }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _session.DisposeCts();
            if (Web.CoreWebView2 != null)
            {
                Web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                Web.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch { }
        _isWebInitialized = false;
    }

    private void ShowFallback(string text)
    {
        LoadingText.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Collapsed;
        FallbackBorder.Visibility = Visibility.Visible;
        FallbackText.Text = text;
    }

    private void PostPlugins(string? extraStatus = null)
    {
        PostToWeb(new { type = "plugins", plugins = _session.GetPluginsPayload(), loadIssues = PluginLoader.Issues });
        if (!string.IsNullOrWhiteSpace(extraStatus))
            PostToWeb(new { type = "status", text = extraStatus });
        else if (PluginLoader.Issues.Count > 0)
            PostToWeb(new { type = "status", text = $"{PluginLoader.Issues.Count} plugin(s) failed to load. See Settings → Tools." });
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = _web.TryRead(e, out var receiveError);
        if (receiveError != null)
        {
            try { PostToWeb(new { type = "error", text = receiveError }); } catch { }
            return;
        }
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "requestSettings":
                    PostToWeb(new { type = "settings", settings = ProviderSettings.RedactForWeb(_session.Settings) });
                    PostPlugins();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (!DefaultLlm.HasKey) return;
                            var quota = await _session.Client.PeekBuiltinQuotaAsync();
                            Dispatcher.Invoke(() => _web.PostQuota(quota));
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
                            _session.Settings = ProviderSettings.MergeFromWeb(_session.Settings, s);
                            ProviderSettings.Save(_session.Settings);
                            _session.RebuildRegistry();
                            PostToWeb(new { type = "settings", settings = ProviderSettings.RedactForWeb(_session.Settings) });
                            PostPlugins(_session.GetStatusText());
                        }
                        break;
                    }

                case "getCatalog":
                    PostToWeb(new { type = "catalog", presets = ProviderCatalog.Presets });
                    break;

                case "testProvider":
                    {
                        var prov = _session.ResolveProviderForTest(doc);
                        if (prov == null) { PostToWeb(new { type = "testResult", ok = false, message = "Provider not found." }); break; }
                        PostToWeb(new { type = "status", text = $"Testing {prov.Name}..." });
                        if (DefaultLlm.IsBuiltin(prov.Id))
                        {
                            _ = Task.Run(async () =>
                            {
                                var quota = await _session.Client.PeekBuiltinQuotaAsync();
                                var msg = quota.Ok
                                    ? $"{DefaultLlm.DisplayName} is reachable. {quota.Remaining} of {quota.Limit} free requests left today."
                                    : quota.Message ?? $"{DefaultLlm.DisplayName} is unavailable.";
                                Dispatcher.Invoke(() =>
                                {
                                    _web.PostQuota(quota);
                                    PostToWeb(new { type = "testResult", ok = quota.Ok, message = msg, providerId = prov.Id, models = new List<string> { DefaultLlm.DisplayModel } });
                                });
                            });
                            break;
                        }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var (ok, msg, models) = await _session.Client.TestAsync(prov);
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
                        var prov = _session.ResolveProviderForTest(doc);
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
                                var list = await _session.Client.ListModelsAsync(prov);
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
                            _session.Settings.Plugins[toolName] = enabled;
                            ProviderSettings.Save(_session.Settings);
                            _session.RebuildRegistry();
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
                            var plugin = _session.FindPlugin(pluginId);
                            if (plugin != null)
                            {
                                foreach (var t in plugin.Meta.Tools) _session.Settings.Plugins[t.Name] = enabled;
                                ProviderSettings.Save(_session.Settings);
                                _session.RebuildRegistry();
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
                            var n = _session.SetTierEnabled(tier, enabled);
                            ProviderSettings.Save(_session.Settings);
                            _session.RebuildRegistry();
                            var l3 = _session.EnabledL3Names().ToList();
                            var msg = enabled ? $"Enabled {n} {tier} tools." : $"Disabled {n} {tier} tools.";
                            if (l3.Count > 0) msg += " L3: " + string.Join(", ", l3) + ".";
                            else if (string.Equals(tier, "L3", StringComparison.OrdinalIgnoreCase)) msg += " No L3 tools on.";
                            PostPlugins(msg);
                        }
                        break;
                    }

                case "saveToolSettings":
                    {
                        ProviderSettings.Save(_session.Settings);
                        _session.RebuildRegistry();
                        var l3 = _session.EnabledL3Names().ToList();
                        var msg = $"Saved. {_session.Registry.GetToolDefinitions().Count} tools on.";
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
                            var plugin = _session.FindPlugin(pluginId);
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
                        if (_session.TryReloadPlugins(out var reloadMsg))
                            PostPlugins(reloadMsg);
                        break;
                    }

                case "approveTool":
                    {
                        var toolCallId = doc.RootElement.TryGetProperty("toolCallId", out var tc) ? tc.GetString() ?? "" : "";
                        var approved = doc.RootElement.TryGetProperty("approved", out var ap) && ap.ValueKind == JsonValueKind.True;
                        _approvals.CompleteApproval(toolCallId, approved);
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
                        _approvals.CompletePreset(toolCallId, approved, valuesJson);
                        PostToWeb(new { type = "status", text = approved ? $"Confirmed preset {toolCallId}" : $"Cancelled preset {toolCallId}" });
                        break;
                    }

                case "presetPick":
                    {
                        var toolCallId = doc.RootElement.TryGetProperty("toolCallId", out var pk) ? pk.GetString() ?? "" : "";
                        var fieldId = doc.RootElement.TryGetProperty("fieldId", out var fid) ? fid.GetString() ?? "" : "";
                        await _approvals.RunPresetPickAsync(toolCallId, fieldId);
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
                        _session.RestoreCheckpoint(msgs);
                        PostToWeb(new { type = "status", text = $"Resumed checkpoint {id} ({msgs.Count} messages)" });
                        PostToWeb(new { type = "history", messages = msgs.Select(m => new { role = m.Role, text = LlmJson.GetContentString(m.Content) }).ToList() });
                        break;
                    }

                case "clear":
                    _session.ClearHistory();
                    break;

                case "cancel":
                    try { _session.CancelAndDeny(); PostToWeb(new { type = "status", text = "Cancelling..." }); _web.PostProgress(ProgressSnapshot.Cancelled()); } catch { }
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
                        await _session.HandleSendAsync(text, mode, slash, mentions, attachments);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            PostToWeb(new { type = "error", text = $"Bridge error: {ex.Message}" });
        }
    }
}
