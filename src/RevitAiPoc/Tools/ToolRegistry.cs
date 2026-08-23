using System.Text.Json;
using RevitAiPoc.Llm;

namespace RevitAiPoc.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ToolEntry entry) => _tools[entry.Definition.Function.Name] = entry;

    public List<ToolDefinition> GetToolDefinitions()
        => _tools.Values.Select(v => v.Definition).ToList();

    public IReadOnlyCollection<ToolEntry> Entries => _tools.Values;

    public bool TryGet(string name, out ToolEntry entry) => _tools.TryGetValue(name, out entry!);
}

public enum ToolTier { L1, L2, L3 }

public sealed class ToolEntry
{
    public ToolDefinition Definition { get; init; } = new();
    public Func<string, Task<string>> Handler { get; init; } = _ => Task.FromResult("");
    public bool RequiresWriteConfirm { get; init; }
    public ToolTier Tier { get; init; } = ToolTier.L1;
}
