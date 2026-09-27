using System.IO;
using LoadingAI.Plugins;
using LoadingAI.Progress;
using LoadingAI.Revit;

namespace LoadingAI.Tests.Fakes;

/// <summary>
/// Minimal <see cref="IPluginContext"/> for tool-handler tests. The host owns every
/// Revit call, so there is no raw access to grant.
/// </summary>
internal sealed class FakePluginContext : IPluginContext
{
    private readonly List<string> _logs = new();

    public FakePluginContext(IRevitBridge bridge, string? pluginDir = null)
    {
        Bridge = bridge;
        PluginDir = pluginDir ?? Path.Combine(Path.GetTempPath(), "LoadingAI.Tests", "plugin");
        RuntimeDataDir = Path.Combine(Path.GetTempPath(), "LoadingAI.Tests", "data");
    }

    public IRevitBridge Bridge { get; }

    public string PluginDir { get; }

    public string RuntimeDataDir { get; }

    public IReadOnlyList<string> Logs => _logs;

    public void Log(string message) => _logs.Add(message);

    public IProgressReporter Progress => ProgressReporter.Current;

    public CancellationToken ShutdownToken => CancellationToken.None;
}
