using System.Text.Json;
using LoadingAI.Llm;

namespace LoadingAI.Tools;

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

public enum PresetFieldKind
{
    String,
    Number,
    Boolean,
    Select,
    PickElements,
    PickBox
}

/// <summary>
/// One host-rendered field shown in the chat pane before the tool handler runs.
/// Optional: omit <see cref="ToolEntry.Presets"/> to keep the current execute path.
/// </summary>
public sealed class PresetField
{
    /// <summary>JSON argument key written on confirm.</summary>
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public PresetFieldKind Kind { get; init; }
    public bool Required { get; init; }
    public object? Default { get; init; }
    /// <summary>Choices for <see cref="PresetFieldKind.Select"/>.</summary>
    public IReadOnlyList<string>? Options { get; init; }
    /// <summary>Optional category hint for <see cref="PresetFieldKind.PickElements"/> (e.g. "Walls").</summary>
    public string? Category { get; init; }
    /// <summary>Status-bar prompt while picking in the model.</summary>
    public string? Prompt { get; init; }
}

public sealed class ToolEntry
{
    public ToolDefinition Definition { get; init; } = new();
    public Func<string, Task<string>> Handler { get; init; } = _ => Task.FromResult("");
    public bool RequiresWriteConfirm { get; init; }
    public ToolTier Tier { get; init; } = ToolTier.L1;
    /// <summary>
    /// Opt-in pre-execute form. Empty (the default) keeps today’s path for existing plugins.
    /// </summary>
    public IReadOnlyList<PresetField> Presets { get; init; } = Array.Empty<PresetField>();
}
