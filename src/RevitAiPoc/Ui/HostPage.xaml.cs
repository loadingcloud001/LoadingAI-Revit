using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using Microsoft.Web.WebView2.Core;
using RevitAiPoc.Llm;
using RevitAiPoc.Plugins.Core;
using RevitAiPoc.Revit;
using RevitAiPoc.Tools;

namespace RevitAiPoc.Ui;

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
    private readonly QueryCache _cache = new();
    private List<DiscoveredPlugin> _discovered = new();
    private AgentLoop? _agent;
    private bool _isRunning;
    private CancellationTokenSource? _cts;
    private string _lastUserText = "";

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
        PostToWeb(new { type = "settings", settings = _settings });
        PostToWeb(new { type = "status", text = GetStatusText() });
        try { PostToWeb(new { type = "plugins", plugins = GetPluginsPayload() }); } catch { }
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
        _discovered = PluginLoader.Discover(bridge, _cache);
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
        _agent = new AgentLoop(_client, _registry);
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
        return active != null ? $"Active: {active.Name} | {active.BaseUrl} | {active.Model}" : "No provider";
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
            if (json.Length > 2 && json[0] == '"' && json[^1] == '"')
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
                    PostToWeb(new { type = "plugins", plugins = GetPluginsPayload() });
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
                            PostToWeb(new { type = "plugins", plugins = GetPluginsPayload() });
                            PostToWeb(new { type = "status", text = GetStatusText() });
                        }
                        break;
                    }

                case "listPlugins":
                    PostToWeb(new { type = "plugins", plugins = GetPluginsPayload() });
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
                            PostToWeb(new { type = "plugins", plugins = GetPluginsPayload() });
                            PostToWeb(new { type = "status", text = enabled ? $"Enabled {toolName}" : $"Disabled {toolName}" });
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
                                PostToWeb(new { type = "plugins", plugins = GetPluginsPayload() });
                                PostToWeb(new { type = "status", text = enabled ? $"Enabled plugin {pluginId}" : $"Disabled plugin {pluginId}" });
                            }
                        }
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
                                var td = new TaskDialog("Revit AI POC")
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
                            _discovered = PluginLoader.Discover(bridge, _cache);
                            RebuildRegistry();
                            PostToWeb(new { type = "plugins", plugins = GetPluginsPayload() });
                            PostToWeb(new { type = "status", text = $"Reloaded {_discovered.Count} plugins, {_registry.GetToolDefinitions().Count} tools enabled." });
                        }
                        break;
                    }

                case "clear":
                    _cts?.Cancel();
                    _agent?.ClearHistory();
                    try { HistoryStore.Append("system", "Cleared."); } catch { }
                    break;

                case "cancel":
                    try { _cts?.Cancel(); PostToWeb(new { type = "status", text = "Cancelling..." }); } catch { }
                    break;

                case "send":
                    {
                        var text = doc.RootElement.GetProperty("text").GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(text)) break;
                        _lastUserText = text;
                        if (_isRunning)
                        {
                            PostToWeb(new { type = "error", text = "Already working, please wait or press Stop." });
                            break;
                        }

                        var provider = ProviderSettings.GetActive(_settings);
                        if (provider == null)
                        {
                            PostToWeb(new { type = "error", text = "No provider configured." });
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(provider.ApiKey))
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
                        _cts?.Dispose();
                        _cts = new CancellationTokenSource();
                        var ct = _cts.Token;
                        var agent = _agent;
                        PostToWeb(new { type = "status", text = "Working..." });

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                string result = await agent.RunAsync(provider, text,
                                    ExecuteToolAsync,
                                    trace =>
                                    {
                                        try { Dispatcher.Invoke(() => PostToWeb(new { type = "trace", text = trace })); } catch { }
                                    },
                                    delta =>
                                    {
                                        try { Dispatcher.Invoke(() => PostToWeb(new { type = "delta", text = delta })); } catch { }
                                    },
                                    ct);

                                Dispatcher.Invoke(() =>
                                {
                                    // If we streamed, the web already has incremental deltas. Avoid duplicate if result equals streamed content.
                                    // For tool-call final answers, deltas may be empty, so post assistant.
                                    if (!string.IsNullOrWhiteSpace(result))
                                    {
                                        // If streamed and result was already streamed, posting again would duplicate.
                                        // We check streamed flag and result length > 0; if streamed, web will dedupe.
                                        PostToWeb(new { type = "assistant", text = result });
                                        try { HistoryStore.Append("assistant", result); HistoryStore.Trim(); } catch { }
                                    }
                                    PostToWeb(new { type = "status", text = GetStatusText() });
                                });
                            }
                            catch (OperationCanceledException)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    PostToWeb(new { type = "error", text = "Cancelled." });
                                    PostToWeb(new { type = "status", text = GetStatusText() });
                                });
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.Invoke(() => PostToWeb(new { type = "error", text = ex.Message }));
                            }
                            finally
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    _isRunning = false;
                                    try { _cts?.Dispose(); _cts = null; } catch { }
                                });
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
            return $"Unknown tool '{name}'.";

        // Dispatch trace for tool call
        try { Dispatcher.Invoke(() => PostToWeb(new { type = "tool_call", text = $"{name}: {argsJson}" })); } catch { }

        string result;
        try
        {
            // Invalidate cache for write tiers before execution? After write we invalidate.
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
                // Invalidate read cache after any write
                _cache.Invalidate();
            }
            catch { }
        }
        return result;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
