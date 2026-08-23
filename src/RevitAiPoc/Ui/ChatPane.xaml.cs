using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevitAiPoc.Llm;
using RevitAiPoc.Tools;

namespace RevitAiPoc.Ui;

public partial class ChatPane : Page
{
    private AppSettings _settings = ProviderSettings.Load();
    private OpenAiCompatibleClient _client = new();
    private ToolRegistry _registry = new();
    private AgentLoop? _agent;
    private bool _isRunning;

    public ChatPane()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = ProviderSettings.Load();
        RefreshProviderCombo();
        InitTools();
        AppendLine("AI Chat ready. Select a provider, add your API key in Settings, then ask e.g. 'How many walls are in this model?'");
        if (ProviderSettings.GetActive(_settings) is { } active)
            StatusText.Text = $"Active: {active.Name} | {active.BaseUrl} | {active.Model}";
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _client.Dispose();
    }

    private void InitTools()
    {
        var bridge = App.Bridge;
        if (bridge == null)
        {
            AppendLine("Error: Revit bridge not ready.");
            return;
        }
        _registry = new ToolRegistry();
        _registry.Register(GetModelSummaryTool.Create(bridge));
        // confirm via bridge TaskDialog, so no WPF callback needed; pass dummy
        _registry.Register(SetCommentsOnSelectionTool.Create(bridge, (_, __) => Task.FromResult(true)));
        _agent = new AgentLoop(_client, _registry);
    }

    private void RefreshProviderCombo()
    {
        ProviderCombo.ItemsSource = null;
        ProviderCombo.ItemsSource = _settings.Providers;
        var active = ProviderSettings.GetActive(_settings);
        if (active != null) ProviderCombo.SelectedItem = active;
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderCombo.SelectedItem is ProviderConfig pc)
        {
            _settings.ActiveProviderId = pc.Id;
            ProviderSettings.Save(_settings);
            StatusText.Text = $"Active: {pc.Name} | {pc.BaseUrl} | {pc.Model}";
        }
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            ProvidersList.ItemsSource = null;
            ProvidersList.ItemsSource = _settings.Providers;
            if (ProviderCombo.SelectedItem is ProviderConfig sel) ProvidersList.SelectedItem = sel;
        }
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

    private void ProvidersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProvidersList.SelectedItem is ProviderConfig pc)
        {
            EditName.Text = pc.Name;
            EditBaseUrl.Text = pc.BaseUrl;
            EditModel.Text = pc.Model;
            EditApiKey.Text = pc.ApiKey;
        }
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var p = new ProviderConfig { Name = "New Provider", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini", ApiKey = "" };
        _settings.Providers.Add(p);
        ProviderSettings.Save(_settings);
        RefreshProviderCombo();
        ProvidersList.ItemsSource = null;
        ProvidersList.ItemsSource = _settings.Providers;
        ProvidersList.SelectedItem = p;
        StatusText.Text = "Added new provider. Fill fields and Save.";
    }

    private void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ProvidersList.SelectedItem is not ProviderConfig pc)
        {
            StatusText.Text = "Select a provider to save.";
            return;
        }
        pc.Name = EditName.Text.Trim();
        pc.BaseUrl = EditBaseUrl.Text.Trim().TrimEnd('/');
        pc.Model = EditModel.Text.Trim();
        pc.ApiKey = EditApiKey.Text.Trim();
        ProviderSettings.Save(_settings);
        RefreshProviderCombo();
        ProvidersList.ItemsSource = null;
        ProvidersList.ItemsSource = _settings.Providers;
        ProvidersList.SelectedItem = pc;
        StatusText.Text = $"Saved {pc.Name}.";
    }

    private void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ProvidersList.SelectedItem is not ProviderConfig pc) return;
        if (_settings.Providers.Count <= 1)
        {
            StatusText.Text = "Cannot delete the last provider.";
            return;
        }
        _settings.Providers.Remove(pc);
        if (_settings.ActiveProviderId == pc.Id) _settings.ActiveProviderId = _settings.Providers[0].Id;
        ProviderSettings.Save(_settings);
        RefreshProviderCombo();
        ProvidersList.ItemsSource = null;
        ProvidersList.ItemsSource = _settings.Providers;
        StatusText.Text = $"Deleted {pc.Name}.";
    }

    private void UseProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ProvidersList.SelectedItem is not ProviderConfig pc) return;
        _settings.ActiveProviderId = pc.Id;
        ProviderSettings.Save(_settings);
        RefreshProviderCombo();
        StatusText.Text = $"Active: {pc.Name}";
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        _agent?.ClearHistory();
        TranscriptBox.Clear();
        AppendLine("Cleared.");
    }

    private async void SendBtn_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        if (_isRunning) return;
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var provider = ProviderSettings.GetActive(_settings);
        if (provider == null)
        {
            AppendLine("No provider configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            AppendLine($"Provider '{provider.Name}' has no API key. Open Settings and paste your key.");
            SettingsPanel.Visibility = Visibility.Visible;
            return;
        }
        if (_agent == null)
        {
            AppendLine("Agent not ready.");
            return;
        }

        AppendLine($"You: {text}");
        InputBox.Clear();
        SetRunning(true);

        try
        {
            var ct = CancellationToken.None;
            string result = await _agent.RunAsync(provider, text, ExecuteToolAsync, trace =>
            {
                Dispatcher.Invoke(() => AppendLine(trace));
            }, ct);

            if (!string.IsNullOrWhiteSpace(result))
                AppendLine($"Assistant: {result}");
        }
        catch (Exception ex)
        {
            AppendLine($"Error: {ex.Message}");
        }
        finally
        {
            SetRunning(false);
        }
    }

    private async Task<string> ExecuteToolAsync(string name, string argsJson)
    {
        if (!_registry.TryGet(name, out var entry))
            return $"Unknown tool '{name}'.";

        // All handlers already marshal via RevitBridge where needed
        try
        {
            return await entry.Handler(argsJson);
        }
        catch (Exception ex)
        {
            return $"Tool {name} failed: {ex.Message}";
        }
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        Dispatcher.Invoke(() =>
        {
            SendBtn.IsEnabled = !running;
            InputBox.IsEnabled = !running;
            StatusText.Text = running ? "Working..." : $"Active: {ProviderSettings.GetActive(_settings)?.Name}";
        });
    }

    private void AppendLine(string line)
    {
        TranscriptBox.AppendText(line + Environment.NewLine + Environment.NewLine);
        TranscriptBox.ScrollToEnd();
        // Also ensure scrollviewer at bottom
        TranscriptScroll.ScrollToEnd();
    }
}
