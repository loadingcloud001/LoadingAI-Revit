using RevitAiPoc.Revit;
using RevitAiPoc.Tools;

namespace RevitAiPoc.Plugins.Core;

/// <summary>
/// MIT opensource plugin contract. Implement in any Plugins DLL.
/// </summary>
public interface IRevitToolPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    /// <summary>Enumerate ToolEntries; loader will filter by enabledMap.</summary>
    IEnumerable<ToolEntry> CreateTools(RevitBridge bridge, QueryCache cache);
}
