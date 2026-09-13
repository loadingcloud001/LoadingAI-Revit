using System;
using System.Threading;
using LoadingAI.Progress;
using LoadingAI.Revit;

namespace LoadingAI.Plugins;

/// <summary>
/// Host-provided context handed to a plugin at load time. Gives safe,
/// revit-free access to the active Revit model by default; raw access is
/// opt-in via <see cref="FullAccess"/> (non-null only for plugins that
/// implement <see cref="IRevitFullAccess"/>).
/// </summary>
public interface IPluginContext
{
    /// <summary>The safe, revit-free bridge contract (typed queries + batch command).</summary>
    IRevitBridge Bridge { get; }

    /// <summary>Directory this plugin was loaded from.</summary>
    string PluginDir { get; }

    /// <summary>
    /// Per-plugin writable data directory under the host's runtime data
    /// folder (created on demand). For caches and large outputs.
    /// </summary>
    string RuntimeDataDir { get; }

    /// <summary>Writes a line to the host plugin log.</summary>
    void Log(string message);

    /// <summary>
    /// Progress for the active AI run. Always non-null (no-op when idle).
    /// Optional — the host already reports thinking / running / done around
    /// every tool. Use <c>Progress.Report("Exporting IFC", 40)</c> only when
    /// a long tool has a known percent.
    /// </summary>
    IProgressReporter Progress { get; }

    /// <summary>Signaled when the host is shutting down or the plugin is being unloaded.</summary>
    CancellationToken ShutdownToken { get; }

    /// <summary>
    /// Opt-in raw Revit access. Null unless this plugin class implements
    /// <see cref="IRevitFullAccess"/> — never null for cooperative plugins.
    /// </summary>
    IRawRevitAccess? FullAccess { get; }
}
