using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.Core;

public sealed class CorePlugin : IRevitToolPlugin
{
    public string Id => "core";
    public string Name => "Core";
    public string Version => "0.1.0";
    public IEnumerable<ToolEntry> CreateTools(RevitBridge bridge, QueryCache cache)
        => AllTools.CreateAll(bridge, cache);
}
