namespace LoadingAI.Ui;

/// <summary>
/// What this host can do, announced to the pane on connect so it shows only controls that work (ADR-0017). A
/// capability is added here in the same commit as the code behind it.
/// </summary>
internal static class HostCapabilities
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        // One card in the pane for every L2 or L3 review; no native dialog behind it.
        "singleGate",
        // The host stores and enforces the permission level.
        "levels",
        // Auto-edit: L2 runs, L3 still asks.
        "autoEdit",
        // Allow for this session / Always allow, Deny with a reason, Edit inputs.
        "reviewScopes",
        // turn_start / tool_start / tool_end / turn_end messages.
        "structuredTurns",
        // turn_changes plus Select, Zoom and Isolate.
        "changes",
        // steer: a message delivered to the agent mid-turn.
        "steer",
        // theme: follow Revit's UI theme.
        "theme",
        // Standing instructions in the system prompt.
        "instructions"
    };
}
