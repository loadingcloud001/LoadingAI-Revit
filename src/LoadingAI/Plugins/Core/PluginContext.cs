using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using LoadingAI.Plugins;
using LoadingAI.Revit;

namespace LoadingAI.Plugins.Core;

/// <summary>
/// App-side implementation of the revit-free plugin context. Wraps the raw
/// <see cref="RevitBridge"/> and exposes the safe <see cref="IRevitBridge"/>
/// contract by default; raw access is opt-in via <see cref="FullAccess"/>.
/// </summary>
public sealed class PluginContext : IPluginContext, IRawRevitAccess
{
    private readonly RevitBridge _raw;
        private readonly string _pluginDir;
        private readonly bool _fullAccessGranted;
        private readonly CancellationTokenSource _shutdown;

        public PluginContext(RevitBridge raw, string pluginDir, bool fullAccessGranted)
        {
            _raw = raw;
            _pluginDir = pluginDir;
        _fullAccessGranted = fullAccessGranted;
        _shutdown = new CancellationTokenSource();
    }

    public IRevitBridge Bridge => new AdapterRevitBridge(_raw);

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

    public IRawRevitAccess? FullAccess => _fullAccessGranted ? this : null;

        /// <summary>Signals this plugin to wind down before unload (cooperative reload gate).</summary>
    internal void BeginShutdown() => _shutdown.Cancel();

    // IRawRevitAccess
    public Task<T> RunRevitAsync<T>(Func<FullAccessSession, T> func, TimeSpan? timeout = null, CancellationToken ct = default)
        => _raw.RunAsync(
            app => func(new FullAccessSession(app)),
            timeout ?? RevitBridge.DefaultTimeout,
            ct);
}
