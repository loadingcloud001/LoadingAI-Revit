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
    /// <summary>Optional authoritative result path. Legacy string handlers remain supported.</summary>
    public Func<string, Task<ToolExecutionResult>>? StructuredHandler { get; init; }
    public async Task<ToolExecutionResult> ExecuteAsync(string argsJson)
    {
        if (!ToolArgumentValidator.Validate(
                Definition.Function.Parameters,
                argsJson,
                out var normalizedJson,
                out var validationError))
            return ToolExecutionResult.Failed(
                "Invalid arguments: " + validationError,
                ToolWriteCertainty.NotCommitted);

        try
        {
            return StructuredHandler != null
                ? await StructuredHandler(normalizedJson)
                : ToolExecutionResult.FromLegacy(await Handler(normalizedJson), Tier);
        }
        catch (BridgeOperationUnknownException ex)
        {
            return ToolExecutionResult.Unknown(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Failed($"Tool {Definition.Function.Name} failed: {ex.Message}");
        }
    }
    /// <summary>
    /// Opt OUT of the generic L2 write-confirm dialog that <c>ToolGuards.ApplyConfirm</c>
    /// would otherwise add. Despite the name, <c>true</c> means this tool does NOT get that
    /// dialog: it owns its own confirmation (L3 host gate, chat preset form) or the host
    /// deliberately confirms it elsewhere. <c>false</c> on an L2 write is what triggers the
    /// "Run X? This will create an undoable transaction." TaskDialog.
    /// This sense is frozen by ADR-0004 and called out as do-not-change in ADR-0006.
    /// Do not invert it without a Proposed ADR first.
    /// </summary>
    public bool RequiresWriteConfirm { get; init; }
    public ToolTier Tier { get; init; } = ToolTier.L1;
    /// <summary>
    /// Opt-in pre-execute form. Empty (the default) keeps today’s path for existing plugins.
    /// </summary>
    public IReadOnlyList<PresetField> Presets { get; init; } = Array.Empty<PresetField>();
    /// <summary>
    /// Catalog tags used by <c>ToolSelector</c> (query, view, write, export, structure, compat).
    /// Empty means the selector infers tags from the tool name.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}
