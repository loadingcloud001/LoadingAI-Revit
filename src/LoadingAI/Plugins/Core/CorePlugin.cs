using System.Collections.Generic;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.Core;

public sealed class CorePlugin : IRevitToolPlugin
{
    public string Id => "core";
    public string Name => "Core";
    public string Version => "0.1.0";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        // Transitional: the builtin tool layer still drives the raw bridge
        // directly (migrated to IRevitBridge in p2-tool-migration).
        var bridge = App.Bridge!;
        var cache = ((PluginContext)context).Cache;
        return AllTools.CreateAll(bridge, cache);
    }
}
