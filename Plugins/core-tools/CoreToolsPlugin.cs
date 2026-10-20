using LoadingAI.Plugins;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.CoreTools;

public sealed class CoreToolsPlugin : IRevitToolPlugin
{
    public string Id => "core-tools";
    public string Name => "Core Tools";
    public string Version => "1.2.0";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
        => AllTools.CreateBridge(context);
}
