namespace LoadingAI.Revit.Formwork;

/// <summary>
/// Formwork defaults the plugin still resolves itself before it hands the call to the bridge,
/// plus the two value lists that a Revit-free tool schema needs to match what the host actually
/// does. Everything else in the formwork configuration is host-side, because the algorithms that
/// read it run in the host. This type lives in Core only so a Revit-free plugin can name it.
/// </summary>
public static class FormworkDefaults
{
    /// <summary>Panel thickness used when the caller passes no <c>thicknessMm</c>.</summary>
    public const double SheetThicknessMmDefault = 18.0;

    /// <summary>
    /// Accepted panel thickness in millimetres. The tool schema declares these as
    /// <c>minimum</c> / <c>maximum</c> so <c>ToolArgumentValidator</c> rejects an out-of-range
    /// value before approval rather than letting the generator build a panel of any size it is
    /// asked for. Bounds rather than an exact value: formwork sheet thickness genuinely varies.
    /// </summary>
    public const double SheetThicknessMmMin = 1.0;
    public const double SheetThicknessMmMax = 100.0;

    /// <summary>
    /// Every cleanup action the host implements. The tool schema declares this as its
    /// <c>enum</c>, so an action the model can send is always one <c>FormworkCleanup.Run</c> has
    /// a case for and the unknown-action rollback is unreachable from chat (ADR-0013).
    /// </summary>
    public static readonly string[] CleanupActions =
    {
        "stats",
        "delete_all",
        "delete_by_type",
        "cleanup_orphans",
    };

    /// <summary>
    /// Every panel face type the generator supports. The plugin builds its <c>faceTypes</c>
    /// schema from this and <c>FormworkConfig.AllFaceTypes</c> aliases it, so the two cannot
    /// drift: a face type added host-side is immediately offered to the model, and one removed
    /// is immediately refused, instead of being silently dropped by the generator.
    /// </summary>
    public static readonly string[] AllFaceTypes =
    {
        "Wall_Exterior",
        "Wall_Interior",
        "Slab_Bottom",
        "Slab_Side",
        "Beam_Bottom",
        "Beam_Side",
        "Column_Side",
        "Foundation_Bottom",
        "Foundation_Side",
        "Stairs_Soffit",
        "Stairs_Side",
    };
}
