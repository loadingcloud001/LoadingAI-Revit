using LoadingAI.Plugins;
using LoadingAI.Revit;

namespace LoadingAI.Plugins.Core;

/// <summary>
/// App-side implementation of <see cref="IPluginContextFactory"/>. Binds each
/// plugin context to the live raw bridge and the shared query cache.
/// </summary>
public sealed class PluginContextFactory : IPluginContextFactory
{
    private readonly RevitBridge _bridge;
    private readonly QueryCache _cache;

    public PluginContextFactory(RevitBridge bridge, QueryCache cache)
    {
        _bridge = bridge;
        _cache = cache;
    }

    public IPluginContext Create(string pluginDir, bool requestFullAccess)
        => new PluginContext(_bridge, _cache, pluginDir, requestFullAccess);
}
