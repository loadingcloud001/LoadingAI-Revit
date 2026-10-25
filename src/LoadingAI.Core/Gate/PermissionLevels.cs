namespace LoadingAI.Gate;

/// <summary>
/// The five user-owned permission levels (ADR-0017). Wire names match <c>PermissionLevel</c> in
/// web/src/state/types.ts. Only the pane's user can change the level; the model cannot.
/// </summary>
public static class PermissionLevels
{
    public const string ReadOnly = "readonly";
    public const string Plan = "plan";
    public const string Confirm = "confirm";
    public const string AutoEdit = "autoEdit";
    public const string Bypass = "bypass";

    public static readonly IReadOnlyList<string> All = new[] { ReadOnly, Plan, Confirm, AutoEdit, Bypass };

    public static bool IsValid(string? level)
        => level != null && All.Contains(level, StringComparer.Ordinal);

    /// <summary>
    /// The level to use for a settings file or message. A valid <paramref name="level"/> wins; an
    /// older file or pane that only knows the <c>autoApprove</c> switch maps true to Bypass and false
    /// to Ask before changes (the old default).
    /// </summary>
    public static string Resolve(string? level, bool legacyAutoApprove)
        => IsValid(level) ? level! : legacyAutoApprove ? Bypass : Confirm;

    /// <summary>The agent mode a level starts a turn in: Read only asks, Plan plans, the rest build.</summary>
    public static string ModeFor(string level) => level switch
    {
        ReadOnly => "ask",
        Plan => "plan",
        _ => "build"
    };

    /// <summary>
    /// The level that governs gating for one turn. A typed <c>/build</c> at Read only or Plan is the
    /// user's explicit request to write, so those turns gate like Ask before changes: every write still
    /// shows a card. Without it, "Run plan" could never write.
    /// </summary>
    public static string EffectiveFor(string level, string? mode)
    {
        if (!IsValid(level)) return Confirm;
        if ((level == ReadOnly || level == Plan) && string.Equals(mode, "build", StringComparison.OrdinalIgnoreCase))
            return Confirm;
        return level;
    }

    /// <summary>Bypass is the only level that skips failure dialogs and every card.</summary>
    public static bool IsBypass(string level) => level == Bypass;
}
