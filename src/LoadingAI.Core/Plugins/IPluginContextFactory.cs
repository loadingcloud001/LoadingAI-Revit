namespace LoadingAI.Plugins;

/// <summary>
/// Creates an <see cref="IPluginContext"/> for a plugin being loaded. The
/// App supplies the concrete implementation (bound to the live bridge and
/// the plugin's folder), keeping <see cref="IPluginContextFactory"/> itself
/// revit-free and loader-agnostic.
/// </summary>
public interface IPluginContextFactory
{
    /// <summary>
    /// Builds a context for the plugin at <paramref name="pluginDir"/>.
    /// <paramref name="requestFullAccess"/> is true when the plugin class
    /// implements <see cref="IRevitFullAccess"/>.
    /// </summary>
    IPluginContext Create(string pluginDir, bool requestFullAccess);
}
