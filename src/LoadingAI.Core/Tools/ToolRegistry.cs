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
    /// <summary>
    /// Shown as a warning line in the <see cref="RunPreview"/> when this field's resolved value
    /// is truthy. Lets a tool call out a dangerous setting without owning a confirm.
    /// </summary>
    public string? Warning { get; init; }
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
    /// Historical flag from ADR-0004, when <c>ToolGuards.ApplyConfirm</c> added a native "Run X?" dialog to
    /// L2 tools that did not opt out. ADR-0017 removed every native dialog: the host now asks once, in chat,
    /// through <c>ApprovalGate.ReviewAsync</c>. Nothing reads this any more; it stays so plugins that set it
    /// still compile.
    /// </summary>
    public bool RequiresWriteConfirm { get; init; }
    /// <summary>
    /// The tool deletes or replaces elements, writes to disk, or overwrites many values at once. The
    /// review card never offers "Always allow" for it, and Auto-edit still asks (ADR-0017). Declared per
    /// tool in plugin.json (<c>"destructive": true</c>) and applied at load.
    /// </summary>
    public bool Destructive { get; init; }
    /// <summary>The tool returns images. It is not offered to a provider that cannot see images.</summary>
    public bool NeedsVision { get; init; }
    /// <summary>
    /// The review card never offers Session or Always allow, and Auto-edit still asks (like
    /// <see cref="Destructive"/>), but the step has no destructive badge. Used for writes that
    /// persist into later prompts, such as save_project_note (ADR-0018).
    /// </summary>
    public bool NeverAllowList { get; init; }
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
    /// <summary>
    /// Optional, read-only summary of what this exact call is about to do: counts, a sample,
    /// before → after. The host shows it in the confirm dialog and on the L3 approval card
    /// (ADR-0015). It must not write the model. Null, empty or a throw falls back to the raw
    /// arguments.
    /// </summary>
    public Func<string, Task<string?>>? Describe { get; init; }
}
