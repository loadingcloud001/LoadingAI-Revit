using Autodesk.Revit.UI;
using LoadingAI.Llm;

namespace LoadingAI.Revit;

/// <summary>
/// Revit's UI theme (light or dark), read once at startup inside the API context and updated by
/// <c>ThemeChanged</c>. The pane asks this cached value, never the API, so it is safe to read from any thread.
/// </summary>
internal static class ThemeWatcher
{
    private static volatile bool _dark = true;

    public static bool IsDark => _dark;

    /// <summary>Raised on Revit's thread when the user switches the Revit UI theme.</summary>
    public static event Action<bool>? Changed;

    public static void Attach(UIControlledApplication app)
    {
        _dark = ReadDark();
        try { app.ThemeChanged += OnThemeChanged; }
        catch (Exception ex) { HostLog.Write($"Theme watcher not attached: {ex.Message}"); }
    }

    public static void Detach(UIControlledApplication app)
    {
        try { app.ThemeChanged -= OnThemeChanged; } catch { }
    }

    private static void OnThemeChanged(object? sender, Autodesk.Revit.UI.Events.ThemeChangedEventArgs e)
    {
        var dark = ReadDark();
        if (dark == _dark) return;
        _dark = dark;
        try { Changed?.Invoke(dark); } catch { }
    }

    private static bool ReadDark()
    {
        try { return UIThemeManager.CurrentTheme == UITheme.Dark; }
        catch { return _dark; }
    }
}
