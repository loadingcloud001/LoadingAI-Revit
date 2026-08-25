using LoadingAI.Tools;

namespace LoadingAI.Plugins;

/// <summary>
/// The contract every plugin entry point implements. Plugins enumerate their
/// tools against the host-supplied <see cref="IPluginContext"/>; the loader
/// filters by the enabled map and binds raw access for opted-in plugins.
/// </summary>
public interface IRevitToolPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }

    /// <summary>Enumerate the plugin's ToolEntries using the given context.</summary>
    IEnumerable<ToolEntry> CreateTools(IPluginContext context);
}
