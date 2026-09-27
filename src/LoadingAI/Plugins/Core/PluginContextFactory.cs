using LoadingAI.Plugins;
using LoadingAI.Revit;

namespace LoadingAI.Plugins.Core;

/// <summary>
/// App-side implementation of <see cref="IPluginContextFactory"/>. Binds each
/// plugin context to the live raw bridge.
/// </summary>
public sealed class PluginContextFactory : IPluginContextFactory
{
    private readonly RevitBridge _bridge;

    public PluginContextFactory(RevitBridge bridge)
    {
        _bridge = bridge;
    }

    public IPluginContext Create(string pluginDir)
        => new PluginContext(_bridge, pluginDir);
}
