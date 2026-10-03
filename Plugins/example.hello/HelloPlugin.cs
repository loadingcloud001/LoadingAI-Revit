// MIT Example plugin — copy this folder to create your own plugin.
// Build: dotnet build -c Release (produces LoadingAI.Plugins.Hello.dll)
// Copy the folder (plugin.json + dll) to %AppData%\Autodesk\Revit\Addins\<year>\LoadingAI\Plugins\dev\hello\ and click Rescan in Settings.
// A DLL placed in %AppData%\LoadingAI\Plugins is refused until you click "Trust this plugin" in Settings.

using System.Threading.Tasks;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.Hello;

public sealed class HelloPlugin : IRevitToolPlugin
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
            // StructuredHandler is the authoritative path the host calls: it returns a
            // ToolExecutionResult carrying the status and the write certainty. Handler is the
            // legacy string path, kept so the same tool body stays callable the old way. Every
            // shipped plugin declares both, and the two must agree on the text.
            Handler = async _ => (await HelloAsync(context)).Text,
            StructuredHandler = _ => HelloAsync(context)
        };
    }

    /// <summary>
    /// The whole tool body, once. This is the pattern to copy: derive the status from what
    /// actually happened rather than from the wording of a returned sentence.
    ///
    /// This tool is L1, so it never writes the model and always reports
    /// <see cref="ToolWriteCertainty.NoWrite"/>. Note that "no document open" is
    /// <c>Failed</c> so the turn timeline shows the read produced nothing, but its certainty is
    /// <c>NoWrite</c> rather than <c>NotCommitted</c>: a read opens no transaction, so there was
    /// nothing to commit or roll back.
    /// </summary>
    private static async Task<ToolExecutionResult> HelloAsync(IPluginContext context)
    {
        context.Progress.Report("Hello…", 40);
        var dto = await context.Bridge.GetModelSummaryAsync();
        if (dto == null)
            return ToolExecutionResult.Failed("Hello! No document open.", ToolWriteCertainty.NoWrite);
        context.Progress.Report("Hello done", 100);
        return ToolExecutionResult.Succeeded(
            $"Hello from Example Plugin! Active doc: {dto.Title}", ToolWriteCertainty.NoWrite);
    }
}
