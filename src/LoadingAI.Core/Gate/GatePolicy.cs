using LoadingAI.Tools;

namespace LoadingAI.Gate;

public enum GateDecision
{
    /// <summary>Run the tool now.</summary>
    Run,
    /// <summary>Show one in-chat review card and wait for the user.</summary>
    Ask,
    /// <summary>Do not run: this level or mode is read-only.</summary>
    Block
}

public sealed class GateRequest
{
    public string Level { get; init; } = PermissionLevels.Confirm;
    /// <summary>The mode the turn started in (ask / plan / build / auto).</summary>
    public string? Mode { get; init; }
    public ToolTier Tier { get; init; }
    public bool Destructive { get; init; }
    public bool DryRun { get; init; }
    public bool HasPresets { get; init; }
    public bool SessionAllowed { get; init; }
    public bool AlwaysAllowed { get; init; }
}

/// <summary>
/// The one place that decides whether a tool call runs, asks, or is blocked (ADR-0017). Pure and
/// side-effect free so it can be tested without Revit. The host acts on the decision; nothing a
/// tool or the model says reaches it.
/// </summary>
public static class GatePolicy
{
    public static GateDecision Decide(GateRequest r)
    {
        if (r.Tier == ToolTier.L1) return GateDecision.Run;
        // A dry run reads and forecasts only, at every level.
        if (r.DryRun) return GateDecision.Run;

        var level = PermissionLevels.EffectiveFor(r.Level, r.Mode);
        if (level == PermissionLevels.ReadOnly || level == PermissionLevels.Plan) return GateDecision.Block;
        if (level == PermissionLevels.Bypass) return GateDecision.Run;
        if (level == PermissionLevels.AutoEdit && r.Tier == ToolTier.L2 && !r.Destructive) return GateDecision.Run;

        return IsAllowlisted(r) ? GateDecision.Run : GateDecision.Ask;
    }

    /// <summary>
    /// An allow-list entry skips the card for that tool. Never for a destructive tool, and never for a
    /// tool with a Run form: the form is where its inputs come from.
    /// </summary>
    public static bool IsAllowlisted(GateRequest r)
        => !r.Destructive && !r.HasPresets && (r.SessionAllowed || r.AlwaysAllowed);

    /// <summary>Whether the review card may offer "Always allow this tool".</summary>
    public static bool CanOfferAlways(bool destructive, bool hasPresets) => !destructive && !hasPresets;
}
