namespace LoadingAI.Gate;

/// <summary>How long an Allow lasts. Wire names match the <c>scope</c> on <c>approveTool</c>.</summary>
public enum ApprovalScope
{
    Once,
    Session,
    Always
}

public static class ApprovalScopes
{
    /// <summary>Unknown or missing text is Once, the narrowest scope.</summary>
    public static ApprovalScope Parse(string? text)
    {
        if (string.Equals(text, "session", StringComparison.OrdinalIgnoreCase)) return ApprovalScope.Session;
        if (string.Equals(text, "always", StringComparison.OrdinalIgnoreCase)) return ApprovalScope.Always;
        return ApprovalScope.Once;
    }
}

/// <summary>
/// Tools the user allowed without a card. The session set lives in memory and is cleared by Clear
/// session, a model change, and a Revit restart (it is never written to disk). The always set is the
/// persisted <c>alwaysAllowTools</c> setting. Thread-safe.
/// </summary>
public sealed class ToolAllowlist
{
    private readonly HashSet<string> _session = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool IsSessionAllowed(string toolName)
    {
        lock (_lock) return _session.Contains(toolName);
    }

    public void AddSession(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return;
        lock (_lock) _session.Add(toolName);
    }

    public void ClearSession()
    {
        lock (_lock) _session.Clear();
    }

    public IReadOnlyList<string> SessionSnapshot()
    {
        lock (_lock) return _session.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The persisted always list after a pane save. The pane may remove entries but never add them:
    /// only a host-side Always allow adds one, so a forged or stale message cannot widen what runs
    /// without a card.
    /// </summary>
    public static List<string> MergeAlways(IEnumerable<string>? current, IEnumerable<string>? incoming)
    {
        var existing = new HashSet<string>(current ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in incoming ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!existing.Contains(name)) continue;
            if (seen.Add(name)) result.Add(name);
        }
        return result;
    }
}
