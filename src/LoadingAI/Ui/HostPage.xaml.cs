using System.IO;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using Microsoft.Web.WebView2.Core;
using LoadingAI.Llm;
using LoadingAI.Plugins.Core;
using LoadingAI.Progress;
using LoadingAI.Protocol;

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

    private void PostToWeb(FromHostMessage payload) => _web.Post(payload);

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

            Web.NavigationCompleted -= OnNavigationCompleted;
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
        PostToWeb(new FromHostMessage.Settings(ProviderSettings.RedactForWeb(_session.Settings)));
        PostToWeb(new FromHostMessage.Status(_session.GetStatusText()));
        try { PostPlugins(); } catch { }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _session.DisposeCts();
            Web.NavigationCompleted -= OnNavigationCompleted;
            if (Web.CoreWebView2 != null)
            {
                Web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
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
        PostToWeb(new FromHostMessage.Plugins(_session.GetPluginsPayload(), PluginLoader.Issues));
        if (!string.IsNullOrWhiteSpace(extraStatus))
            PostToWeb(new FromHostMessage.Status(extraStatus));
        else if (PluginLoader.FailureCount > 0)
            PostToWeb(new FromHostMessage.Status($"{PluginLoader.FailureCount} plugin(s) failed to load. See Settings → Tools."));
    }

    /// <summary>
    /// Stage 2 trust click. The web sends the plugin identifier only; the host re-hashes the DLL bytes
    /// here and compares them to the hash recorded by the scan that refused it, so the bytes trusted are
    /// the bytes the engineer was shown. Writes once through <see cref="PluginAllowlist.TryAddTrustedHash"/>
    /// — no internal retry, no loop. Trusting only records a hash: tool tiers and Confirm L3 are unchanged.
    /// </summary>
    private void TrustPlugin(string folder, string assembly)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(assembly))
        {
            PostToWeb(new FromHostMessage.Error("Trust refused: plugin folder and assembly are required."));
            return;
        }

        string dll, pluginFolder;
        try
        {
            pluginFolder = Path.GetFullPath(folder);
            dll = Path.GetFullPath(Path.Combine(pluginFolder, Path.GetFileName(assembly)));
        }
        catch (Exception ex)
        {
            PostToWeb(new FromHostMessage.Error($"Trust refused: {ex.Message}"));
            return;
        }

        if (!PluginAllowlist.IsUnderDirectory(dll, PluginLoader.RuntimePluginsDir))
        {
            PostToWeb(new FromHostMessage.Error($"Trust refused: {dll} is not in {PluginLoader.RuntimePluginsDir}."));
            return;
        }
        if (!File.Exists(dll))
        {
            PostToWeb(new FromHostMessage.Error($"Trust refused: {dll} not found."));
            return;
        }

        var assemblyName = Path.GetFileName(dll);
        var recorded = PluginLoader.Issues.FirstOrDefault(i =>
            !string.IsNullOrWhiteSpace(i.Sha256)
            && string.Equals(i.Assembly, assemblyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.Folder, pluginFolder, StringComparison.OrdinalIgnoreCase));
        if (recorded == null)
        {
            PostToWeb(new FromHostMessage.Error("Trust refused: no refused DLL recorded for this row. Rescan, then click Trust this plugin… again."));
            return;
        }

        string sha256;
        try { sha256 = PluginAllowlist.HashFile(dll); }
        catch (Exception ex)
        {
            PostToWeb(new FromHostMessage.Error($"Trust refused: cannot read {dll}: {ex.Message}"));
            return;
        }

        if (!string.Equals(sha256, recorded.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            PostToWeb(new FromHostMessage.Error($"Trust refused: {assemblyName} changed since the last scan. Rescan, then click Trust this plugin… again."));
            return;
        }

        var name = string.IsNullOrWhiteSpace(recorded.PluginId) ? Path.GetFileName(pluginFolder) : recorded.PluginId;
        var td = new TaskDialog("LoadingAI for Revit")
        {
            MainInstruction = $"Trust plugin {name}?",
            MainContent = $"{dll}\nSHA-256 {sha256}\n\nLets this DLL load in Revit. Tool tiers and Confirm L3 are unchanged.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No
        };
        if (td.Show() != TaskDialogResult.Yes)
        {
            PostToWeb(new FromHostMessage.Status("Trust cancelled."));
            return;
        }

        if (!PluginAllowlist.Current.TryAddTrustedHash(sha256, out var error))
        {
            // Verbatim, never swallowed: a false treated as "nothing happened" is a silent Trust click.
            PostToWeb(new FromHostMessage.Error(error));
            PostToWeb(new FromHostMessage.Status(TrustRetryHint(error)));
            return;
        }

        if (!_session.TryReloadPlugins(out var reloadMsg))
        {
            PostToWeb(new FromHostMessage.Status($"Trusted {name}. {reloadMsg}"));
            return;
        }
        PostPlugins($"Trusted {name}. {reloadMsg}");
    }

    /// <summary>
    /// Writer aborts carrying the "refusing to overwrite" prefix are file-state, not caller bugs: a
    /// concurrent read holding FileShare.Read blocks the delete inside File.Replace. Those can clear on
    /// their own, so the engineer is offered a retry. The retry is the next Trust click — this never
    /// re-attempts the write. Classification stays coarse: the prefix is shared by the unreadable-existing
    /// and write-denied aborts, so it is deliberately not a fork between them.
    /// </summary>
    private static string TrustRetryHint(string error)
        => error.StartsWith("refusing to overwrite", StringComparison.OrdinalIgnoreCase)
            ? "Nothing was written. Click Trust this plugin… again to retry."
            : "Nothing was written.";

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = _web.TryRead(e, out var receiveError);
        if (receiveError != null)
        {
            try { PostToWeb(new FromHostMessage.Error(receiveError)); } catch { }
            return;
        }
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            if (!ToHostMessage.TryParse(json, out var msg, out var parseError))
            {
                PostToWeb(new FromHostMessage.Error($"Bridge error: {parseError}"));
                return;
            }
            if (msg == null) return;

            switch (msg)
            {
                case ToHostMessage.RequestSettings:
                    PostToWeb(new FromHostMessage.Settings(ProviderSettings.RedactForWeb(_session.Settings)));
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

                case ToHostMessage.SaveSettings { Settings: var s }:
                    if (s != null)
                    {
                        _session.Settings = ProviderSettings.MergeFromWeb(_session.Settings, s);
                        ProviderSettings.Save(_session.Settings);
                        _session.RebuildRegistry();
                        PostToWeb(new FromHostMessage.Settings(ProviderSettings.RedactForWeb(_session.Settings)));
                        PostPlugins(_session.GetStatusText());
                    }
                    break;

                case ToHostMessage.GetCatalog:
                    PostToWeb(new FromHostMessage.Catalog(ProviderCatalog.Presets));
                    break;

                case ToHostMessage.TestProvider test:
                    {
                        var prov = _session.ResolveProviderForTest(test.ProviderId, test.Provider);
                        if (prov == null) { PostToWeb(new FromHostMessage.TestResult(false, "Provider not found.")); break; }
                        PostToWeb(new FromHostMessage.Status($"Testing {prov.Name}..."));
                        if (DefaultLlm.IsBuiltin(prov.Id))
                        {
                            _ = Task.Run(async () =>
                            {
                                var quota = await _session.Client.PeekBuiltinQuotaAsync();
                                var msgText = quota.Ok
                                    ? $"{DefaultLlm.DisplayName} is reachable. {quota.Remaining} of {quota.Limit} free requests left today."
                                    : quota.Message ?? $"{DefaultLlm.DisplayName} is unavailable.";
                                Dispatcher.Invoke(() =>
                                {
                                    _web.PostQuota(quota);
                                    PostToWeb(new FromHostMessage.TestResult(quota.Ok, msgText, prov.Id, new List<string> { DefaultLlm.DisplayModel }));
                                });
                            });
                            break;
                        }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var (ok, testMsg, models) = await _session.Client.TestAsync(prov);
                                Dispatcher.Invoke(() => PostToWeb(new FromHostMessage.TestResult(ok, testMsg, prov.Id, models)));
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.Invoke(() => PostToWeb(new FromHostMessage.TestResult(false, ex.Message, prov.Id)));
                            }
                        });
                        break;
                    }

                case ToHostMessage.ListModels list:
                    {
                        var prov = _session.ResolveProviderForTest(list.ProviderId, list.Provider);
                        if (prov == null) { PostToWeb(new FromHostMessage.Models(Array.Empty<string>(), ErrorText: "Provider not found.")); break; }
                        if (DefaultLlm.IsBuiltin(prov.Id))
                        {
                            PostToWeb(new FromHostMessage.Models(new[] { DefaultLlm.DisplayModel }, prov.Id));
                            break;
                        }
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var models = await _session.Client.ListModelsAsync(prov);
                                Dispatcher.Invoke(() => PostToWeb(new FromHostMessage.Models(models, prov.Id)));
                            }
                            catch (Exception ex) { Dispatcher.Invoke(() => PostToWeb(new FromHostMessage.Models(Array.Empty<string>(), prov.Id, ex.Message))); }
                        });
                        break;
                    }

                case ToHostMessage.ListPlugins:
                    PostPlugins();
                    break;

                case ToHostMessage.SetPluginEnabled { ToolName: var toolName, Enabled: var enabled }:
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        _session.Settings.Plugins[toolName] = enabled;
                        ProviderSettings.Save(_session.Settings);
                        _session.RebuildRegistry();
                        PostPlugins(enabled ? $"Enabled {toolName}" : $"Disabled {toolName}");
                    }
                    break;

                case ToHostMessage.SetPluginGroupEnabled { PluginId: var pluginId, Enabled: var enabled }:
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

                case ToHostMessage.SetPluginTierEnabled { Tier: var tier, Enabled: var enabled }:
                    if (!string.IsNullOrWhiteSpace(tier))
                    {
                        if (enabled && string.Equals(tier, "L3", StringComparison.OrdinalIgnoreCase))
                        {
                            var td = new TaskDialog("LoadingAI for Revit")
                            {
                                MainInstruction = "Enable all L3 tools?",
                                MainContent = "Turns on delete, move, create, and save for chat. Each L3 action still asks Confirm L3 (default No). This does not write the model yet.",
                                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                                DefaultButton = TaskDialogResult.No
                            };
                            if (td.Show() != TaskDialogResult.Yes)
                            {
                                PostToWeb(new FromHostMessage.Status("L3 all on cancelled."));
                                break;
                            }
                        }
                        var n = _session.SetTierEnabled(tier, enabled);
                        ProviderSettings.Save(_session.Settings);
                        _session.RebuildRegistry();
                        var l3 = _session.EnabledL3Names().ToList();
                        var status = enabled ? $"Enabled {n} {tier} tools." : $"Disabled {n} {tier} tools.";
                        if (l3.Count > 0) status += " L3: " + string.Join(", ", l3) + ".";
                        else if (string.Equals(tier, "L3", StringComparison.OrdinalIgnoreCase)) status += " No L3 tools on.";
                        PostPlugins(status);
                    }
                    break;

                case ToHostMessage.SaveToolSettings:
                    {
                        ProviderSettings.Save(_session.Settings);
                        _session.RebuildRegistry();
                        var l3 = _session.EnabledL3Names().ToList();
                        var saved = $"Saved. {_session.Registry.GetToolDefinitions().Count} tools on.";
                        if (l3.Count > 0) saved += " L3: " + string.Join(", ", l3) + ".";
                        else saved += " No L3 tools on — use L3 all on.";
                        PostPlugins(saved);
                        break;
                    }

                case ToHostMessage.RemovePlugin { PluginId: var pluginId }:
                    {
                        var plugin = string.IsNullOrWhiteSpace(pluginId) ? null : _session.FindPlugin(pluginId);
                        if (plugin == null)
                        {
                            PostToWeb(new FromHostMessage.Error($"Plugin {pluginId} not found or is built-in."));
                            break;
                        }
                        if (!PluginLoader.CanRemove(plugin.Meta.Id, plugin.Meta.FolderPath, out var refusal))
                        {
                            PostToWeb(new FromHostMessage.Error(refusal));
                            break;
                        }
                        if (!Directory.Exists(plugin.Meta.FolderPath))
                        {
                            PostToWeb(new FromHostMessage.Error($"Plugin {pluginId} not found or is built-in."));
                            break;
                        }
                        var td = new TaskDialog("LoadingAI for Revit")
                        {
                            MainInstruction = $"Remove plugin {pluginId}?",
                            MainContent = plugin.Meta.FolderPath,
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                            DefaultButton = TaskDialogResult.No
                        };
                        if (td.Show() != TaskDialogResult.Yes)
                        {
                            PostToWeb(new FromHostMessage.Status("Remove cancelled."));
                            break;
                        }
                        try { Directory.Delete(plugin.Meta.FolderPath, true); PostToWeb(new FromHostMessage.Status($"Removed {pluginId}. Restart Revit to unload.")); }
                        catch (Exception ex) { PostToWeb(new FromHostMessage.Error($"Remove failed: {ex.Message}. Close Revit and delete manually: {plugin.Meta.FolderPath}")); }
                        break;
                    }

                case ToHostMessage.ReloadPlugins:
                    if (_session.TryReloadPlugins(out var reloadMsg))
                        PostPlugins(reloadMsg);
                    else
                        PostToWeb(new FromHostMessage.Status(reloadMsg));
                    break;

                case ToHostMessage.TrustPlugin { Folder: var trustFolder, Assembly: var trustAssembly }:
                    TrustPlugin(trustFolder, trustAssembly);
                    break;

                case ToHostMessage.ApproveTool { ToolCallId: var toolCallId, Approved: var approved }:
                    _approvals.CompleteApproval(toolCallId, approved);
                    PostToWeb(new FromHostMessage.Status(approved ? $"Approved {toolCallId}" : $"Denied {toolCallId}"));
                    break;

                case ToHostMessage.SubmitPreset { ToolCallId: var toolCallId, Approved: var approved, ValuesJson: var valuesJson }:
                    _approvals.CompletePreset(toolCallId, approved, valuesJson);
                    PostToWeb(new FromHostMessage.Status(approved ? $"Confirmed preset {toolCallId}" : $"Cancelled preset {toolCallId}"));
                    break;

                case ToHostMessage.PresetPick { ToolCallId: var toolCallId, FieldId: var fieldId }:
                    await _approvals.RunPresetPickAsync(toolCallId, fieldId);
                    break;

                case ToHostMessage.ListCheckpoints:
                    {
                        var cps = CheckpointStore.List();
                        PostToWeb(new FromHostMessage.Checkpoints(cps));
                        break;
                    }

                case ToHostMessage.ResumeCheckpoint { Id: var id }:
                    if (_session.IsRunning) { PostToWeb(new FromHostMessage.Error("Stop the current run before resuming a checkpoint.")); break; }
                    if (string.IsNullOrWhiteSpace(id)) { PostToWeb(new FromHostMessage.Error("Checkpoint id required.")); break; }
                    var msgs = CheckpointStore.Load(id);
                    if (msgs == null) { PostToWeb(new FromHostMessage.Error($"Checkpoint {id} not found.")); break; }
                    _session.RestoreCheckpoint(msgs);
                    PostToWeb(new FromHostMessage.Status($"Resumed checkpoint {id} ({msgs.Count} messages)"));
                    PostToWeb(new FromHostMessage.History(msgs.Select(m => new FromHostMessage.HistoryMessage(m.Role, LlmJson.GetContentString(m.Content))).ToList()));
                    break;

                case ToHostMessage.Clear:
                    _session.ClearHistory();
                    break;

                case ToHostMessage.Cancel:
                    try { _session.CancelAndDeny(); PostToWeb(new FromHostMessage.Status("Cancelling...")); _web.PostProgress(ProgressSnapshot.Cancelled()); } catch { }
                    break;

                case ToHostMessage.Send send:
                    foreach (var err in send.AttachmentErrors)
                        PostToWeb(new FromHostMessage.Error(err));
                    await _session.HandleSendAsync(send.Text, send.Mode, send.Slash, send.Mentions.ToList(), send.Attachments.ToList());
                    break;
            }
        }
        catch (Exception ex)
        {
            PostToWeb(new FromHostMessage.Error($"Bridge error: {ex.Message}"));
        }
    }
}
