// MIT Example plugin — copy this folder to create your own plugin.
// Build: dotnet build -c Release (produces RevitAiPoc.Plugins.Hello.dll)
// Drop the folder (plugin.json + dll) into %AppData%\RevitAiPoc\Plugins\hello\ and restart Revit.

using RevitAiPoc.Llm;
using RevitAiPoc.Plugins.Core;
using RevitAiPoc.Revit;
using RevitAiPoc.Tools;

namespace RevitAiPoc.Plugins.Hello;

public sealed class HelloPlugin : IRevitToolPlugin
{
    public string Id => "hello";
    public string Name => "Hello Example";
    public string Version => "0.1.0";

    public IEnumerable<ToolEntry> CreateTools(RevitBridge bridge, QueryCache cache)
    {
        yield return new ToolEntry
        {
            Tier = ToolTier.L1,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "hello",
                    Description = "Example plugin tool: returns hello + active doc title.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                var r = await bridge.RunAsync(uiApp =>
                {
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Hello! No document open.";
                    return $"Hello from Example Plugin! Active doc: {doc.Title}";
                });
                return r;
            }
        };
    }
}
