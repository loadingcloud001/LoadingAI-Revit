using System;
using System.IO;
using System.Threading;
using LoadingAI.Plugins;
using LoadingAI.Progress;
using LoadingAI.Revit;

namespace LoadingAI.Plugins.Core;

/// <summary>
/// App-side implementation of the revit-free plugin context. Wraps the raw
/// <see cref="RevitBridge"/> and exposes only the safe <see cref="IRevitBridge"/>
/// contract — the host owns every Revit call.
/// </summary>
public sealed class PluginContext : IPluginContext
{
    private readonly RevitBridge _raw;
    private readonly IRevitBridge _bridge;
    private readonly string _pluginDir;
    private readonly CancellationTokenSource _shutdown;

    public PluginContext(RevitBridge raw, string pluginDir)
    {
        _raw = raw;
        _bridge = new AdapterRevitBridge(_raw);
        _pluginDir = pluginDir;
        _shutdown = new CancellationTokenSource();
    }

    public IProgressReporter Progress => ProgressReporter.Current;

    public IRevitBridge Bridge => _bridge;

    public string PluginDir => _pluginDir;

    public string RuntimeDataDir
    {
        get
        {
            var id = Path.GetFileName(_pluginDir) ?? "plugin";
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "PluginData", id);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Log(string message) => PluginLogs.Append(_pluginDir, message);

    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>Signals this plugin to wind down before unload (cooperative reload gate).</summary>
    internal void BeginShutdown() => _shutdown.Cancel();
}
