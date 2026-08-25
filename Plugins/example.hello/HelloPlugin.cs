// MIT Example plugin — copy this folder to create your own plugin.
// Build: dotnet build -c Release (produces LoadingAI.Plugins.Hello.dll)
// Drop the folder (plugin.json + dll) into %AppData%\LoadingAI\Plugins\hello\ and restart Revit.

using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.Hello;

public sealed class HelloPlugin : IRevitToolPlugin, IRevitFullAccess
{
    public string Id => "hello";
    public string Name => "Hello Example";
    public string Version => "0.1.0";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
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
                return await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Hello! No document open.";
                    return $"Hello from Example Plugin! Active doc: {doc.Title}";
                });
            }
        };
    }
}
