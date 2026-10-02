namespace LoadingAI.Tools;

/// <summary>One line of the pre-run preview: a setting and the value it is set to.</summary>
public sealed class PreviewRow
{
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>
/// Read-only preview of what a preset tool call is set up to do, derived from the
/// <see cref="PresetField"/> declarations plus the resolved arguments.
/// <para>
/// Not a gate. It opens no transaction, runs before the preset form, and changes
/// nothing about the ADR-0004 confirm sequence (ADR-014).
/// </para>
/// </summary>
public sealed class RunPreview
{
    public string Summary { get; init; } = "";
    public IReadOnlyList<PreviewRow> Rows { get; init; } = Array.Empty<PreviewRow>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
