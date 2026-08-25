namespace LoadingAI.Plugins;

/// <summary>
/// Optional. Implement on your plugin class to cooperate with hot reload
/// (Revit 2025+ ALC): the loader waits until the plugin reports idle before
/// unloading its assembly, avoiding a crash mid-call. On Revit 2024
/// (net48, no unloadable ALC) this is advisory only — a restart fallback is
/// used instead.
/// </summary>
public interface IPluginLifecycle
{
    /// <summary>
    /// True while the plugin is executing work that must not be interrupted
    /// by an unload. The loader polls this before discarding the assembly.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// The maximum time in milliseconds the loader should wait for
    /// <see cref="IsBusy"/> to become false before giving up on the unload.
    /// Default 2000 when the interface is implemented without overriding.
    /// </summary>
    int MaxReloadWaitMs { get; }
}

/// <summary>
/// Default cooperative reload-gate policy when a plugin implements
/// <see cref="IPluginLifecycle"/> but does not override the wait.
/// </summary>
public static class PluginLifecycleDefaults
{
    public const int MaxReloadWaitMs = 2000;
}
