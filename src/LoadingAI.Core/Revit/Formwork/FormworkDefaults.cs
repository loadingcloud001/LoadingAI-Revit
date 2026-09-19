namespace LoadingAI.Revit.Formwork;

/// <summary>
/// Formwork defaults the plugin still resolves itself before it hands the call to the bridge.
/// Everything else in the formwork configuration is host-side, because the algorithms that read
/// it run in the host. This type lives in Core only so a Revit-free plugin can name it.
/// </summary>
public static class FormworkDefaults
{
    /// <summary>Panel thickness used when the caller passes no <c>thicknessMm</c>.</summary>
    public const double SheetThicknessMmDefault = 18.0;
}
